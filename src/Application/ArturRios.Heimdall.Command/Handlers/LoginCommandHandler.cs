using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Random;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="LoginCommand" /> (UC-11): locates the person by the lookup their role
///     implies (FR-AU-01/02), verifies the password against the stored hash and salt, confirms
///     neither the person nor the scope backing them is logically deleted (FR-AU-05/06/07), and
///     issues a token carrying their <c>PublicId</c>, role, and scope claims (FR-AU-03/04) — unless
///     the person has active two-factor authentication, in which case AF-11g diverts to a short-lived
///     challenge token instead (FR-2F-07…FR-2F-08; see UC-38 for how the login is then completed).
/// </summary>
/// <remarks>
///     AF-11a…AF-11e all return the same <see cref="AuthMessages.InvalidCredentials" /> error, so the
///     endpoint reveals nothing about which emails exist or which accounts and scopes are deleted.
///     The checks nonetheless run in the specification's order, so the code reads against UC-11.
/// </remarks>
public class LoginCommandHandler(
    IValidator<LoginCommand> validator,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncReadOnlyRepository<TwoFactorAuth> twoFactorReader,
    IAsyncReadOnlyRepository<TwoFactorEmailCode> emailCodeReader,
    IAsyncRepository<TwoFactorEmailCode> emailCodeWriter,
    ITwoFactorEmailSender twoFactorEmailSender,
    ITwoFactorChallengeTokenIssuer challengeTokenIssuer,
    PersonAuthTokenService personAuthTokenService)
    : ICommandHandlerAsync<LoginCommand, LoginCommandOutput>
{
    private static readonly TimeSpan EmailCodeLifetime = TimeSpan.FromMinutes(10);

    public async Task<DataOutput<LoginCommandOutput?>> HandleAsync(LoginCommand command)
    {
        var output = DataOutput<LoginCommandOutput?>.New;

        // AF-11f: validate input shape.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // UC-11 step 2. The lookup deliberately omits an !IsDeleted filter: AF-11c exists to reject a
        // logically deleted person, so they must be found first.
        var person = await FindPersonAsync(command);

        // AF-11a.
        if (person is null)
        {
            return output.WithError(AuthMessages.InvalidCredentials);
        }

        // UC-11 step 3 (AF-11b).
        if (!Hash.TextMatches(command.Password, person.PasswordHash, person.Salt))
        {
            return output.WithError(AuthMessages.InvalidCredentials);
        }

        // UC-11 step 4 (AF-11c, FR-AU-05).
        if (person.IsDeleted)
        {
            return output.WithError(AuthMessages.InvalidCredentials);
        }

        // UC-11 step 5 (AF-11d/AF-11e, FR-AU-06/07).
        if (!personAuthTokenService.TryBuildSubject(person, out var subject))
        {
            return output.WithError(AuthMessages.InvalidCredentials);
        }

        // UC-11 step 6, AF-11g (FR-2F-07): an active 2FA configuration diverts to a challenge token.
        var twoFactorAuth = await twoFactorReader.Query()
            .FirstOrDefaultAsync(x => x.PersonId == person.Id && x.IsActive);

        if (twoFactorAuth is not null)
        {
            return await IssueChallengeAsync(output, person, twoFactorAuth);
        }

        var token = await personAuthTokenService.IssueAsync(subject!);

        return output
            .WithData(new LoginCommandOutput
            {
                Token = token.Token, ExpiresAt = token.ExpiresAt, EmailVerified = person.EmailVerified
            })
            .WithMessage(AuthMessages.LoginSuccessful);
    }

    /// <summary>
    ///     AF-11g: issues the short-lived challenge token instead of a full one, and — per FR-2F-08 —
    ///     a fresh email code when the Email method is enabled, the same way
    ///     <c>EnableTwoFactorAuthCommandHandler</c> retires prior outstanding codes before issuing a
    ///     new one.
    /// </summary>
    private async Task<DataOutput<LoginCommandOutput?>> IssueChallengeAsync(
        DataOutput<LoginCommandOutput?> output, Person person, TwoFactorAuth twoFactorAuth)
    {
        if (twoFactorAuth.EmailEnabled)
        {
            var emailCodeErrors = await IssueFreshEmailCodeAsync(twoFactorAuth, person.Email);

            if (emailCodeErrors is not null)
            {
                return output.WithErrors(emailCodeErrors);
            }
        }

        var challenge = await challengeTokenIssuer.IssueAsync(person.PublicId, (int)person.RoleId);

        var methods = new List<string>();

        if (twoFactorAuth.AppEnabled)
        {
            methods.Add("App");
        }

        if (twoFactorAuth.EmailEnabled)
        {
            methods.Add("Email");
        }

        return output
            .WithData(new LoginCommandOutput
            {
                RequiresTwoFactor = true, ChallengeToken = challenge.Token, AvailableMethods = methods
            })
            .WithMessage(AuthMessages.TwoFactorRequired);
    }

    /// <summary>
    ///     Marks every not-yet-used, not-yet-expired email code for this configuration as used, then
    ///     issues and mails a fresh 6-digit one — mirroring
    ///     <c>EnableTwoFactorAuthCommandHandler.RetireOutstandingCodesAsync</c> and its code-issuing
    ///     step, so only the code just mailed for this login attempt can confirm it (FR-2F-08).
    /// </summary>
    private async Task<IEnumerable<string>?> IssueFreshEmailCodeAsync(TwoFactorAuth twoFactorAuth, string email)
    {
        var now = DateTime.UtcNow;

        var live = await emailCodeReader.Query()
            .Where(x => x.TwoFactorAuthId == twoFactorAuth.Id && !x.Used && x.ExpiresAt > now)
            .ToListAsync();

        foreach (var outstanding in live)
        {
            outstanding.Used = true;

            var retirement = await emailCodeWriter.UpdateAsync(outstanding);

            if (!retirement.Success)
            {
                return retirement.Errors;
            }
        }

        var code = CustomRandom.Text(new RandomStringOptions
        {
            Length = 6,
            IncludeDigits = true,
            IncludeLowercase = false,
            IncludeUppercase = false,
            IncludeSpecialCharacters = false
        });

        var codeHash = Hash.EncodeWithRandomSalt(code, out var salt);

        var creation = await emailCodeWriter.CreateAsync(new TwoFactorEmailCode
        {
            TwoFactorAuthId = twoFactorAuth.Id,
            CodeHash = codeHash,
            Salt = salt,
            ExpiresAt = now.Add(EmailCodeLifetime),
            Used = false
        });

        if (!creation.Success)
        {
            return creation.Errors;
        }

        // Delivery failures are not this endpoint's business to surface, the same way
        // EnableTwoFactorAuthCommandHandler treats them: the code is already persisted, and a caller
        // who receives nothing can try to log in again.
        await twoFactorEmailSender.SendAsync(email, code);

        return null;
    }

    /// <summary>
    ///     UC-11 step 2: a <c>User</c> is sought within the scope the request names, since their
    ///     email is only unique there (FR-AU-01); a <c>ScopeAdmin</c>/<c>SystemAdmin</c> is sought
    ///     system-wide among admins (FR-AU-02). Emails are compared case-insensitively (LOWER() in
    ///     SQL), matching how uniqueness is enforced when a person is created.
    /// </summary>
    private async Task<Person?> FindPersonAsync(LoginCommand command)
    {
        var email = command.Email.ToLower();

        var query = personReader.Query()
            .Include(person => person.ScopeMembership)
            .ThenInclude(membership => membership!.Scope)
            .Include(person => person.ScopeOwnerships)
            .ThenInclude(ownership => ownership.Scope);

        if (command.ScopeId is null)
        {
            return await query.FirstOrDefaultAsync(person =>
                person.Email.ToLower() == email &&
                (person.RoleId == (long)Roles.SystemAdmin || person.RoleId == (long)Roles.ScopeAdmin));
        }

        return await query.FirstOrDefaultAsync(person =>
            person.Email.ToLower() == email &&
            person.RoleId == (long)Roles.User &&
            person.ScopeMembership != null &&
            person.ScopeMembership.Scope.PublicId == command.ScopeId);
    }
}
