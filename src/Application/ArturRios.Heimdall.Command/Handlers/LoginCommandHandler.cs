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
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="LoginCommand" /> (UC-11): locates the person by the lookup their role
///     implies (FR-AU-01/02), verifies the password against the stored hash and salt, confirms
///     neither the person nor the scope backing them is logically deleted (FR-AU-05/06/07), and
///     issues a token carrying their <c>PublicId</c>, role, and scope claims (FR-AU-03/04).
/// </summary>
/// <remarks>
///     AF-11a…AF-11e all return the same <see cref="AuthMessages.InvalidCredentials" /> error, so the
///     endpoint reveals nothing about which emails exist or which accounts and scopes are deleted.
///     The checks nonetheless run in the specification's order, so the code reads against UC-11.
/// </remarks>
public class LoginCommandHandler(
    IValidator<LoginCommand> validator,
    IAsyncReadOnlyRepository<Person> personReader,
    IAuthTokenIssuer tokenIssuer)
    : ICommandHandlerAsync<LoginCommand, LoginCommandOutput>
{
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
        var liveOwnedScopeIds = person.ScopeOwnerships
            .Where(ownership => !ownership.Scope.IsDeleted)
            .Select(ownership => ownership.Scope.PublicId)
            .ToList();

        if (person.RoleId == (long)Roles.User && person.ScopeMembership!.Scope.IsDeleted)
        {
            return output.WithError(AuthMessages.InvalidCredentials);
        }

        if (person.RoleId == (long)Roles.ScopeAdmin && liveOwnedScopeIds.Count == 0)
        {
            return output.WithError(AuthMessages.InvalidCredentials);
        }

        // UC-11 step 6 (FR-AU-03/04). A User claims the scope they belong to, a Scope Admin the live
        // scopes they own, a System Admin neither — a deleted scope is never claimed.
        var token = await tokenIssuer.IssueAsync(new AuthTokenSubject(
            person.PublicId,
            (int)person.RoleId,
            person.RoleId == (long)Roles.User ? person.ScopeMembership!.Scope.PublicId : null,
            liveOwnedScopeIds));

        return output
            .WithData(new LoginCommandOutput { Token = token.Token, ExpiresAt = token.ExpiresAt })
            .WithMessage(AuthMessages.LoginSuccessful);
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
