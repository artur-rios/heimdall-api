using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Command.Services;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Command.Handlers;

/// <summary>
///     Handles <see cref="PasswordRecoveryCommand" /> (UC-12): locates the person by the lookup
///     their role implies, then issues a time-limited reset token and has it emailed to them
///     (FR-PR-01/02).
/// </summary>
/// <remarks>
///     <para>
///         Every path returns the same success output. AF-12a — the address belongs to nobody — is
///         not an error flow here but the absence of one: the handler simply issues no token and
///         answers exactly as it would have. A person who is logically deleted, or a <c>User</c>
///         whose scope is, is treated the same way, for the reason UC-11 gives at length: an
///         endpoint open to anonymous callers must not become a directory of which addresses are
///         registered and which accounts still exist.
///     </para>
///     <para>
///         The only thing that distinguishes the two paths is a row that does not get written, and
///         the email that consequently never arrives — neither of which is visible to the caller.
///     </para>
/// </remarks>
public class PasswordRecoveryCommandHandler(
    IValidator<PasswordRecoveryCommand> validator,
    IAsyncReadOnlyRepository<Person> personReader,
    IPasswordResetService passwordReset)
    : ICommandHandlerAsync<PasswordRecoveryCommand, PasswordRecoveryCommandOutput>
{
    public async Task<DataOutput<PasswordRecoveryCommandOutput?>> HandleAsync(PasswordRecoveryCommand command)
    {
        var output = DataOutput<PasswordRecoveryCommandOutput?>.New;

        // NFR-10: validate input shape. The only rejection this endpoint ever issues.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // UC-12 step 2. As in UC-11, the lookup omits an !IsDeleted filter so a deleted person is
        // found and then declined below, rather than being indistinguishable from a missing row for
        // the wrong reason.
        var person = await FindPersonAsync(command);

        // UC-12 steps 3 and 4. Skipped entirely for AF-12a and for accounts that could not log in
        // anyway — sending a reset link to a deleted person's address would restore nothing.
        if (person is not null && MayRecover(person))
        {
            await passwordReset.IssueAndSendAsync(person);
        }

        // UC-12 step 5: the same answer either way.
        return output
            .WithData(new PasswordRecoveryCommandOutput())
            .WithMessage(AuthMessages.PasswordRecoveryRequested);
    }

    /// <summary>
    ///     Mirrors the account checks UC-11 applies at login (FR-AU-05/06/07). A reset link is only
    ///     worth issuing to someone who could use the resulting password.
    /// </summary>
    private static bool MayRecover(Person person)
    {
        if (person.IsDeleted)
        {
            return false;
        }

        return person.RoleId switch
        {
            (long)Roles.User => !person.ScopeMembership!.Scope.IsDeleted,
            (long)Roles.ScopeAdmin => person.ScopeOwnerships.Any(ownership => !ownership.Scope.IsDeleted),
            _ => true
        };
    }

    /// <summary>
    ///     UC-12 step 2, the same role-driven lookup as UC-11: a <c>User</c> is sought within the
    ///     scope the request names, since their email is only unique there; a <c>ScopeAdmin</c> or
    ///     <c>SystemAdmin</c> system-wide among admins. Emails are compared case-insensitively
    ///     (LOWER() in SQL), matching how uniqueness is enforced when a person is created.
    /// </summary>
    private async Task<Person?> FindPersonAsync(PasswordRecoveryCommand command)
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
