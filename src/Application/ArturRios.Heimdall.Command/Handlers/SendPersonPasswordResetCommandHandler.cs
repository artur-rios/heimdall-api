using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="SendPersonPasswordResetCommand" /> (UC-52, FR-PR-05/06): an administrator has
///     a password-reset link mailed to a person's own address.
/// </summary>
/// <remarks>
///     <para>
///         Eligibility is UC-12's, through <see cref="PasswordRecoveryEligibility" />, so the anonymous
///         recovery and this request cannot disagree about who may recover. Refusals, in order: not
///         found (AF-52a); the caller may not act on this person (AF-52b); restricted, then deleted
///         (AF-52c); the account could not sign in (AF-52d); the cooldown is running (AF-52e).
///     </para>
///     <para>
///         The send is made here, not through UC-12's anonymous queue: that queue exists so an
///         anonymous caller cannot tell a registered address from an unknown one by timing, and an
///         authenticated administrator is owed a real answer instead. Earlier live reset links are left
///         as <see cref="IPasswordResetService.IssueAndSendAsync" /> leaves them for UC-12.
///     </para>
/// </remarks>
public class SendPersonPasswordResetCommandHandler(
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<PasswordResetToken, long> tokenReader,
    IScopeOwnershipChecker scopeOwnership,
    IPasswordResetService passwordReset,
    PasswordResetOptions resetOptions,
    AdminEmailOptions adminEmailOptions)
    : ICommandHandlerAsync<SendPersonPasswordResetCommand, SendPersonPasswordResetCommandOutput>
{
    public async Task<DataOutput<SendPersonPasswordResetCommandOutput?>> HandleAsync(
        SendPersonPasswordResetCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<SendPersonPasswordResetCommandOutput?>.New;

        // AF-52a. Both scope navigations are loaded: the eligibility rule reads them.
        var person = await personReader.Query()
            .Include(x => x.ScopeMembership)
            .ThenInclude(membership => membership!.Scope)
            .Include(x => x.ScopeOwnerships)
            .ThenInclude(ownership => ownership.Scope)
            .FirstOrDefaultAsync(x => x.PublicId == command.Id, cancellationToken);

        if (person is null)
        {
            return output.WithError(PersonMessages.PersonNotFound);
        }

        // AF-52b.
        if (!await PersonEmailAuthorization.ActorMayEmailAsync(
                scopeOwnership, command.ActingRole, command.ActingPersonId, person))
        {
            return output.WithError(PersonMessages.NotAuthorizedToEmailPerson);
        }

        // AF-52c and AF-52d: UC-12's rule, reported rather than silently applied.
        switch (PasswordRecoveryEligibility.Of(person))
        {
            case RecoveryEligibility.Restricted:
                return output.WithError(PersonMessages.PersonProcessingRestricted);
            case RecoveryEligibility.Deleted:
                return output.WithError(PersonMessages.PersonIsDeleted);
            case RecoveryEligibility.CannotSignIn:
                return output.WithError(PersonMessages.PersonCannotSignIn);
        }

        // AF-52e. Any reset token issued for the person inside the window — UC-12's included, used
        // or not — means one was sent recently.
        // Best-effort: concurrent requests for the same person can both pass this check (no lock).
        // Accepted, because only an authenticated administrator can trigger it.
        if (await adminEmailOptions.IsRunningAsync(
                tokenReader, person.Id, resetOptions.TokenLifetime, DateTime.UtcNow, cancellationToken))
        {
            return output.WithError(PersonMessages.AccountEmailSentRecently);
        }

        await passwordReset.IssueAndSendAsync(person);

        return output
            .WithData(new SendPersonPasswordResetCommandOutput { Id = person.PublicId })
            .WithMessage(PersonMessages.PasswordResetSentToPerson);
    }
}
