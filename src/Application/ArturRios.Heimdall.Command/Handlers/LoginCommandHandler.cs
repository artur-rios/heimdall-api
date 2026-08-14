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
///     <para>
///         AF-11a…AF-11e all return the same <see cref="AuthMessages.InvalidCredentials" /> error, so
///         the endpoint reveals nothing about which emails exist or which accounts and scopes are
///         deleted. The checks nonetheless run in the specification's order, so the code reads
///         against UC-11.
///     </para>
///     <para>
///         AF-11a additionally verifies the submitted password against a decoy hash before answering.
///         Argon2id is deliberately expensive — 600 MB and 16 threads by the hashing library's
///         default — so a request that skipped it because no person matched returned in single-digit
///         milliseconds while every other rejection took hundreds. That gap is readable from outside
///         and answers the exact question the shared message exists to refuse: whether an address is
///         registered, and (by varying <see cref="LoginCommand.ScopeId" />) which scope it sits in.
///         One uniform message is not anti-enumeration on its own if the timing disagrees with it.
///     </para>
/// </remarks>
public class LoginCommandHandler(
    IValidator<LoginCommand> validator,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Person> personWriter,
    IAsyncReadOnlyRepository<TwoFactorAuth> twoFactorReader,
    IAsyncReadOnlyRepository<TwoFactorEmailCode> emailCodeReader,
    IAsyncRepository<TwoFactorEmailCode> emailCodeWriter,
    ITwoFactorEmailSender twoFactorEmailSender,
    ITwoFactorChallengeTokenIssuer challengeTokenIssuer,
    PersonAuthTokenService personAuthTokenService)
    : ICommandHandlerAsync<LoginCommand, LoginCommandOutput>
{
    private const int MaxFailedLoginAttempts = 10;

    private static readonly TimeSpan EmailCodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    // A hash of a random secret nobody knows, used only to spend the same Argon2id work on AF-11a
    // that a real password check spends. Generated per process rather than hard-coded so it is never
    // a value an attacker could recognise, and computed once so the cost sits at start-up rather
    // than on the request that needs to look like every other request.
    private static readonly (byte[] Hash, byte[] Salt) Decoy = CreateDecoy();

    private static (byte[] Hash, byte[] Salt) CreateDecoy()
    {
        var hash = Hash.EncodeWithRandomSalt(
            CustomRandom.Text(new RandomStringOptions { Length = 32 }), out var salt);

        return (hash, salt);
    }

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

        // AF-11a. The decoy verification is what keeps this path indistinguishable from the ones
        // below by response time — see the remarks. Its result is discarded: the answer is already
        // decided, and only the work matters.
        if (person is null)
        {
            VerifyAgainstDecoy(command.Password);

            return output.WithError(AuthMessages.InvalidCredentials);
        }

        // A locked-out account is refused before its password is even considered (FR-AU-09). The
        // decoy keeps the cost of that refusal equal to a real check's, so a lockout cannot be
        // detected by how quickly it answers.
        if (person.LockedOutUntil is { } lockedUntil && lockedUntil > DateTime.UtcNow)
        {
            VerifyAgainstDecoy(command.Password);

            return output.WithError(AuthMessages.InvalidCredentials);
        }

        // UC-11 step 3 (AF-11b).
        if (!Hash.TextMatches(command.Password, person.PasswordHash, person.Salt))
        {
            await RecordFailedAttemptAsync(person);

            return output.WithError(AuthMessages.InvalidCredentials);
        }

        await ClearFailedAttemptsAsync(person);

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
    ///     Counts a wrong password and, at <see cref="MaxFailedLoginAttempts" /> consecutive misses,
    ///     locks the account for <see cref="LockoutDuration" /> (FR-AU-09).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The per-IP limiter in <c>Startup</c> bounds how fast one source can guess; this bounds
    ///         how many guesses an account will accept in total, which is the half a distributed
    ///         attacker defeats by spreading requests across addresses.
    ///     </para>
    ///     <para>
    ///         The lockout is a window rather than a latch that an administrator has to clear: a
    ///         permanent lock would hand any anonymous caller a denial of service against any account
    ///         whose address they know, since reaching the threshold needs nothing but wrong
    ///         passwords. Fifteen minutes cuts a sustained guessing rate to a few hundred attempts a
    ///         day while costing a caller who genuinely mistyped their password one coffee break.
    ///     </para>
    ///     <para>
    ///         A failure to persist the counter is swallowed rather than surfaced. It is
    ///         bookkeeping — the credentials were wrong either way, and UC-11 defines no flow in
    ///         which a caller is told that the count of their failures could not be written.
    ///     </para>
    /// </remarks>
    private async Task RecordFailedAttemptAsync(Person person)
    {
        person.FailedLoginAttempts++;

        if (person.FailedLoginAttempts >= MaxFailedLoginAttempts)
        {
            person.LockedOutUntil = DateTime.UtcNow.Add(LockoutDuration);
            person.FailedLoginAttempts = 0;
        }

        await personWriter.UpdateAsync(person);
    }

    /// <summary>
    ///     Clears the failure counter once the password checks out, so the threshold counts
    ///     consecutive failures rather than every failure the account has ever had. Nothing is written
    ///     when there is nothing to clear, keeping an ordinary login read-only on this table.
    /// </summary>
    private async Task ClearFailedAttemptsAsync(Person person)
    {
        if (person is { FailedLoginAttempts: 0, LockedOutUntil: null })
        {
            return;
        }

        person.FailedLoginAttempts = 0;
        person.LockedOutUntil = null;

        await personWriter.UpdateAsync(person);
    }

    /// <summary>
    ///     Runs one password verification against a hash that belongs to nobody, so that AF-11a costs
    ///     what AF-11b costs. The salt and hash are computed once, at type initialisation, from a
    ///     random secret no one holds; only the per-request Argon2id derivation is repeated, which is
    ///     the whole of the expense being matched.
    /// </summary>
    private static void VerifyAgainstDecoy(string password) =>
        Hash.TextMatches(password, Decoy.Hash, Decoy.Salt);

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
