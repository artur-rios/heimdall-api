using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="AddScopeOwnerCommand" /> (UC-21, FR-SC-08/FR-SC-09): verifies the target
///     scope exists and is active (AF-21a), enforces scope ownership for a Scope Admin actor
///     (AF-21c), verifies the named person is an existing, non-deleted <c>ScopeAdmin</c> or
///     <c>SystemAdmin</c> (AF-21b), then links them to the scope with a <c>SCOPE_OWNER</c> row —
///     serving a person who already owns it as an idempotent no-op (AF-21d). A Scope Admin actor gets
///     an invitation instead (FR-SC-14, Threat Model TH-41): the person owns nothing until they accept
///     it (UC-60). A System Admin actor bypasses the ownership check. All failures are returned as
///     errors on the <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
public class AddScopeOwnerCommandHandler(
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IAsyncReadOnlyRepository<Person, long> personReader,
    IScopeOwnershipChecker scopeOwnership,
    IAtomicWrites atomicWrites,
    IAsyncReadOnlyRepository<ScopeOwnerInvitation, long> invitationReader,
    IScopeOwnerInvitations invitations,
    ScopeInvitationOptions invitationOptions,
    AdminEmailOptions adminEmail,
    IScopeInvitationNoticeSender notices,
    IAuditLogWriter auditLog,
    ILogger<AddScopeOwnerCommandHandler> logger)
    : ICommandHandlerAsync<AddScopeOwnerCommand, AddScopeOwnerCommandOutput>
{
    public async Task<DataOutput<AddScopeOwnerCommandOutput?>> HandleAsync(AddScopeOwnerCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<AddScopeOwnerCommandOutput?>.New;

        // AF-21a (UC-21 step 2): the target scope must exist and not be logically deleted — the
        // alternative flow names both conditions as one outcome, so the filter answers for both.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ScopeId && !x.IsDeleted);

        if (scope is null)
        {
            return output.WithError(PersonMessages.ScopeNotFound);
        }

        // AF-21c: a Scope Admin actor may only act on a scope they own; a System Admin bypasses.
        // Checked before the person is looked up so a caller who fails here learns nothing about
        // which person ids exist or what roles they hold.
        if (!await scopeOwnership.ActorMayManageScopeAsync(command.ActingRole, command.ActingPersonId, scope.Id))
        {
            return output.WithError(PersonMessages.NotScopeOwner);
        }

        // AF-21b (UC-21 step 3): the person must exist, not be logically deleted, and hold an
        // administrator role — ScopeAdmin or SystemAdmin (FR-SC-08). A deleted person can no longer
        // authenticate, so an ownership granted to them could never be exercised. All three
        // conditions share one answer, so the endpoint cannot be used to tell an unknown id from a
        // User. The ownership rows are included because adding to that collection is how the join
        // row gets written below.
        var person = await personReader.Query()
            .Include(x => x.ScopeOwnerships)
            .FirstOrDefaultAsync(x =>
                x.PublicId == command.PersonId && !x.IsDeleted &&
                (x.RoleId == (long)Roles.ScopeAdmin || x.RoleId == (long)Roles.SystemAdmin));

        if (person is null)
        {
            return output.WithError(PersonMessages.PersonNotValidScopeAdmin);
        }

        // AF-21d: already an owner, so there is nothing to write — UC-21 step 4 calls the insert a
        // no-op in that case.
        if (person.ScopeOwnerships.Any(ownership => ownership.ScopeId == scope.Id))
        {
            return AlreadyOwner(output, scope, person);
        }

        // TH-41 (FR-SC-14): a Scope Admin no longer makes another administrator an owner on their own. The
        // person is invited and owns nothing until they accept, signed in as themselves (UC-60). A System
        // Admin administers every scope already, and UC-06 onboarding relies on their immediate add, so
        // their path below is unchanged.
        if (command.ActingRole != (int)Roles.SystemAdmin)
        {
            return await InviteAsync(output, command, scope, person, cancellationToken);
        }

        // UC-21 step 4: insert the SCOPE_OWNER row, by a targeted write of that row alone (#237) rather
        // than a save of the person aggregate, which would write the person's profile back as read.
        // Made only while the person is still a live administrator, so a deletion or a role change
        // that landed since the check above is not overruled. FR-PE-11 needs no guard here: the person
        // is already an administrator and this only ever increases the scopes they own.
        if (!await atomicWrites.TryAddScopeOwnerAsync(person, scope.Id))
        {
            // Refused: either another request made them an owner meanwhile, which is AF-21d's outcome,
            // or they stopped being a live administrator, which is AF-21b's. The row tells which.
            var ownsNow = await personReader.Query()
                .AnyAsync(x => x.Id == person.Id && x.ScopeOwnerships.Any(ownership => ownership.ScopeId == scope.Id));

            return ownsNow
                ? AlreadyOwner(output, scope, person)
                : output.WithError(PersonMessages.PersonNotValidScopeAdmin);
        }

        // UC-21 step 5.
        return output
            .WithData(new AddScopeOwnerCommandOutput
            {
                ScopeId = scope.PublicId, PersonId = person.PublicId, AlreadyOwner = false
            })
            .WithMessage(PersonMessages.ScopeOwnerAddedSuccessfully);
    }

    /// <summary>
    ///     Opens an invitation for <paramref name="person" />, or finds the one already open, audits a new one,
    ///     and mails the invitee a notice when it is new — unless the administrator-mail rules hold it back.
    /// </summary>
    /// <remarks>
    ///     <see cref="IScopeOwnerInvitations.OpenAsync" /> saves the unit of work, so nothing on this path
    ///     changes a tracked entity before it: the scope, the person and the inviter are read for decisions
    ///     only, and the invitation is the one entity it adds.
    /// </remarks>
    private async Task<DataOutput<AddScopeOwnerCommandOutput?>> InviteAsync(
        DataOutput<AddScopeOwnerCommandOutput?> output, AddScopeOwnerCommand command, Scope scope, Person person,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var inviter = await personReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ActingPersonId, cancellationToken);

        var opening = await invitations.OpenAsync(new ScopeOwnerInvitation
        {
            PublicId = Guid.NewGuid(),
            ScopeId = scope.Id,
            InviteePersonId = person.Id,
            InvitedByPersonId = inviter?.Id,
            CreatedAt = now,
            ExpiresAt = now + invitationOptions.Lifetime
        }, now);

        var invitation = opening.Invitation;

        if (opening.Created)
        {
            await AuditCreatedAsync(invitation);
        }

        // The administrator-mail rules hold the notice back, never the invitation. A repeat while one is
        // open mails nothing (the idempotent re-invite). A restricted person is mailed nothing (AF-51c,
        // NFR-24); their acceptance would void it anyway. And a new one mails nothing when the invitee was
        // invited within the cooldown: withdrawing and re-inviting must not become a way to fill their inbox.
        if (opening.Created && person.ProcessingRestrictedAt is null &&
            !await InvitedRecentlyAsync(person.Id, invitation.Id, now, cancellationToken))
        {
            await notices.SendInvitationAsync(
                person.Email, inviter?.Name ?? "An administrator", scope.Name, invitation.ExpiresAt);
        }

        return output
            .WithData(new AddScopeOwnerCommandOutput
            {
                ScopeId = scope.PublicId,
                PersonId = person.PublicId,
                AlreadyOwner = false,
                Invited = true,
                InvitationId = invitation.PublicId,
                InvitationExpiresAt = invitation.ExpiresAt
            })
            .WithMessage(ScopeInvitationMessages.InvitationSent);
    }

    /// <summary>
    ///     Whether another invitation to the same person was created within
    ///     <c>HEIMDALL_ADMIN_EMAIL_COOLDOWN_IN_SECONDS</c> — the administrator-mail rule, derived from the
    ///     invitation rows as <see cref="AdminEmailOptions" /> derives it from token rows.
    /// </summary>
    private Task<bool> InvitedRecentlyAsync(
        long inviteeId, long invitationId, DateTime now, CancellationToken cancellationToken)
    {
        var since = now - adminEmail.Cooldown;

        return invitationReader.Query()
            .AnyAsync(x => x.InviteePersonId == inviteeId && x.Id != invitationId && x.CreatedAt > since,
                cancellationToken);
    }

    /// <summary>
    ///     Records the new invitation (NFR-09) with the invitation as target, which the decorator's entry for
    ///     this command cannot carry. Like the decorator, a failed write is logged and the answer stands.
    /// </summary>
    private async Task AuditCreatedAsync(ScopeOwnerInvitation invitation)
    {
        try
        {
            await auditLog.WriteAsync(ScopeOwnerInvitationAuditActions.Created, invitation.PublicId, true, null);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception, "Failed to audit the creation of invitation {InvitationId}", invitation.PublicId);
        }
    }

    private static DataOutput<AddScopeOwnerCommandOutput?> AlreadyOwner(
        DataOutput<AddScopeOwnerCommandOutput?> output, Scope scope, Person person) =>
        output
            .WithData(new AddScopeOwnerCommandOutput
            {
                ScopeId = scope.PublicId, PersonId = person.PublicId, AlreadyOwner = true
            })
            .WithMessage(PersonMessages.AlreadyScopeOwner);
}
