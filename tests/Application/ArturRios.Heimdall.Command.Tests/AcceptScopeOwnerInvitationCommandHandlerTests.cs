using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for AcceptScopeOwnerInvitationCommandHandler (UC-60, FR-SC-17): the invitee accepting an open
// invitation owns the scope and the inviter is told; the inviter, an outsider, an ended or expired invitation,
// and an acceptance that loses a race are all AF-60a; an invitation that can no longer be honoured — scope
// deleted, invitee no longer an unrestricted administrator, before the read or between the read and the
// write — is voided, audited, and answered as AF-60a; an inviter who is gone is not told.
public class AcceptScopeOwnerInvitationCommandHandlerTests
{
    private static AcceptScopeOwnerInvitationCommandHandler Handler(InvitationWorld world, IAuditLogWriter? audit = null) =>
        new(world.Persons, world.Scopes, world.Invitations, world.Store, world.Notices,
            audit ?? new RecordingAuditLogWriter(), NullLogger<AcceptScopeOwnerInvitationCommandHandler>.Instance);

    private static AcceptScopeOwnerInvitationCommand Command(Person actor, ScopeOwnerInvitation invitation) => new()
    {
        InvitationId = invitation.PublicId, ActingPersonId = actor.PublicId, ActingRole = (int)Roles.ScopeAdmin
    };

    private static async Task<(InvitationWorld World, Scope Scope, Person Inviter, Person Invitee, ScopeOwnerInvitation Invitation)>
        OpenInvitationAsync(Action<ScopeOwnerInvitation>? beforeConditionalWrite = null)
    {
        var world = new InvitationWorld(beforeConditionalWrite);
        var scope = await world.ScopeAsync();
        var inviter = await world.PersonAsync("Ada", owns: scope);
        var invitee = await world.PersonAsync("Grace");
        var invitation = await world.InvitationAsync(scope, invitee, inviter);
        return (world, scope, inviter, invitee, invitation);
    }

    [UnitFact]
    public async Task GivenAnOpenInvitation_WhenTheInviteeAccepts_ThenTheyOwnTheScopeAndTheInviterIsTold()
    {
        // Given
        var (world, scope, inviter, invitee, invitation) = await OpenInvitationAsync();

        // When
        var output = await Handler(world).HandleAsync(Command(invitee, invitation));

        // Then
        Assert.True(output.Success);
        Assert.Contains(ScopeInvitationMessages.InvitationAccepted, output.Messages);
        Assert.Equal(invitation.PublicId, output.Data!.Id);
        Assert.Equal(scope.PublicId, output.Data.ScopeId);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Accepted, invitation.Outcome);
        Assert.Contains(invitee.ScopeOwnerships, ownership => ownership.ScopeId == scope.Id);
        var answer = Assert.Single(world.Notices.Answers);
        Assert.Equal((inviter.Email, invitee.Name, scope.Name, true), answer);
    }

    [UnitFact]
    public async Task GivenTheInviter_WhenAccepting_ThenNotOpenAndNothingChanges()
    {
        // Given — the spec: inviter, invitee and outsider are told the same
        var (world, scope, inviter, _, invitation) = await OpenInvitationAsync();

        // When
        var output = await Handler(world).HandleAsync(Command(inviter, invitation));

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Null(invitation.EndedAt);
        Assert.Single(inviter.ScopeOwnerships, ownership => ownership.ScopeId == scope.Id);
    }

    [UnitFact]
    public async Task GivenAnOutsider_WhenAccepting_ThenNotOpenAndNothingChanges()
    {
        // Given
        var (world, _, _, _, invitation) = await OpenInvitationAsync();
        var outsider = await world.PersonAsync("Alan");

        // When
        var output = await Handler(world).HandleAsync(Command(outsider, invitation));

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Null(invitation.EndedAt);
        Assert.Empty(outsider.ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenAWithdrawalLandingBeforeTheWrite_WhenAccepting_ThenNotOpenAndNoOwnership()
    {
        // Given — the race between accept and withdraw: an owner withdraws after the handler read it
        var audit = new RecordingAuditLogWriter();
        var (world, scope, _, invitee, invitation) = await OpenInvitationAsync(beforeConditionalWrite: racing =>
        {
            racing.EndedAt = DateTime.UtcNow;
            racing.Outcome = ScopeOwnerInvitationOutcomes.Withdrawn;
        });

        // When
        var output = await Handler(world, audit).HandleAsync(Command(invitee, invitation));

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Withdrawn, invitation.Outcome);
        Assert.DoesNotContain(invitee.ScopeOwnerships, ownership => ownership.ScopeId == scope.Id);
        Assert.Empty(world.Notices.Answers);
        Assert.Empty(audit.Entries);
    }

    [UnitFact]
    public async Task GivenAnExpiredInvitation_WhenAccepting_ThenNotOpen()
    {
        // Given
        var world = new InvitationWorld();
        var scope = await world.ScopeAsync();
        var invitee = await world.PersonAsync("Grace");
        var invitation = await world.InvitationAsync(scope, invitee, null,
            createdAt: DateTime.UtcNow.AddDays(-8), expiresAt: DateTime.UtcNow.AddDays(-1));

        // When
        var output = await Handler(world).HandleAsync(Command(invitee, invitation));

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Empty(invitee.ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenADeletedScope_WhenAccepting_ThenTheInvitationIsVoidedAuditedAndAnsweredAsNotOpen()
    {
        // Given AF-60b
        var (world, scope, _, invitee, invitation) = await OpenInvitationAsync();
        scope.IsDeleted = true;
        var audit = new RecordingAuditLogWriter();

        // When
        var output = await Handler(world, audit).HandleAsync(Command(invitee, invitation));

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Voided, invitation.Outcome);
        Assert.Equal([(ScopeOwnerInvitationAuditActions.Voided, (Guid?)invitation.PublicId, true)], audit.Entries);
        Assert.Empty(invitee.ScopeOwnerships);
        Assert.Empty(world.Notices.Answers);
    }

    [UnitFact]
    public async Task GivenAnInviteeDemotedToUser_WhenAccepting_ThenVoided()
    {
        // Given AF-60b — UC-08 made them a User after they were invited
        var (world, scope, _, invitee, invitation) = await OpenInvitationAsync();
        invitee.RoleId = (long)Roles.User;
        var audit = new RecordingAuditLogWriter();

        // When
        var output = await Handler(world, audit).HandleAsync(Command(invitee, invitation));

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Voided, invitation.Outcome);
        Assert.Equal([(ScopeOwnerInvitationAuditActions.Voided, (Guid?)invitation.PublicId, true)], audit.Entries);
        Assert.DoesNotContain(invitee.ScopeOwnerships, ownership => ownership.ScopeId == scope.Id);
    }

    [UnitFact]
    public async Task GivenARestrictedInvitee_WhenAccepting_ThenVoided()
    {
        // Given AF-60b — NFR-24
        var (world, scope, _, invitee, invitation) = await OpenInvitationAsync();
        invitee.ProcessingRestrictedAt = DateTime.UtcNow.AddMinutes(-5);
        var audit = new RecordingAuditLogWriter();

        // When
        var output = await Handler(world, audit).HandleAsync(Command(invitee, invitation));

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Voided, invitation.Outcome);
        Assert.Equal([(ScopeOwnerInvitationAuditActions.Voided, (Guid?)invitation.PublicId, true)], audit.Entries);
        Assert.DoesNotContain(invitee.ScopeOwnerships, ownership => ownership.ScopeId == scope.Id);
    }

    [UnitFact]
    public async Task GivenAnInviteeDemotedBetweenTheReadAndTheWrite_WhenAccepting_ThenVoidedAuditedAndNotOpen()
    {
        // Given AF-60b landing late (G-F28): the pre-check passed, then UC-08 made the invitee a User just
        // before the acceptance's conditional write, which refuses it
        var audit = new RecordingAuditLogWriter();
        var (world, scope, _, invitee, invitation) = await OpenInvitationAsync(beforeConditionalWrite: racing =>
            racing.Invitee.RoleId = (long)Roles.User);

        // When
        var output = await Handler(world, audit).HandleAsync(Command(invitee, invitation));

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Voided, invitation.Outcome);
        Assert.Equal([(ScopeOwnerInvitationAuditActions.Voided, (Guid?)invitation.PublicId, true)], audit.Entries);
        Assert.DoesNotContain(invitee.ScopeOwnerships, ownership => ownership.ScopeId == scope.Id);
        Assert.Empty(world.Notices.Answers);
    }

    [UnitFact]
    public async Task GivenAScopeDeletedBetweenTheReadAndTheWrite_WhenAccepting_ThenVoidedAndNotOpen()
    {
        // Given AF-60b landing late (G-F28): the scope is deleted just before the conditional write
        var audit = new RecordingAuditLogWriter();
        var (world, _, _, invitee, invitation) = await OpenInvitationAsync(beforeConditionalWrite: racing =>
            racing.Scope.IsDeleted = true);

        // When
        var output = await Handler(world, audit).HandleAsync(Command(invitee, invitation));

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Voided, invitation.Outcome);
        Assert.Equal([(ScopeOwnerInvitationAuditActions.Voided, (Guid?)invitation.PublicId, true)], audit.Entries);
        Assert.Empty(invitee.ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenTheVoidsAuditFails_WhenAccepting_ThenTheVoidStandsAndTheAnswerIsNotOpen()
    {
        // Given AF-60b with an audit store that refuses the write
        var (world, scope, _, invitee, invitation) = await OpenInvitationAsync();
        scope.IsDeleted = true;

        // When
        var output = await Handler(world, new FailingAuditLogWriter()).HandleAsync(Command(invitee, invitation));

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Voided, invitation.Outcome);
    }

    [UnitFact]
    public async Task GivenAnInvitationWhoseInviterWasCleared_WhenAccepting_ThenAcceptedAndNobodyIsTold()
    {
        // Given — Review Focus 4: the inviter was anonymised or hard-deleted
        var world = new InvitationWorld();
        var scope = await world.ScopeAsync();
        var invitee = await world.PersonAsync("Grace");
        var invitation = await world.InvitationAsync(scope, invitee, null);

        // When
        var output = await Handler(world).HandleAsync(Command(invitee, invitation));

        // Then
        Assert.True(output.Success);
        Assert.Contains(invitee.ScopeOwnerships, ownership => ownership.ScopeId == scope.Id);
        Assert.Empty(world.Notices.Answers);
    }

    [UnitFact]
    public async Task GivenAnInviterWhoseRowIsGone_WhenAccepting_ThenAcceptedAndNobodyIsTold()
    {
        // Given — the inviter was hard-deleted and the invitation still names their id
        var world = new InvitationWorld();
        var scope = await world.ScopeAsync();
        var invitee = await world.PersonAsync("Grace");
        var gone = new Person { Id = 9_999, PublicId = Guid.NewGuid(), Name = "Gone", Email = "gone@test.local" };
        var invitation = await world.InvitationAsync(scope, invitee, gone);

        // When
        var output = await Handler(world).HandleAsync(Command(invitee, invitation));

        // Then
        Assert.True(output.Success);
        Assert.Contains(invitee.ScopeOwnerships, ownership => ownership.ScopeId == scope.Id);
        Assert.Empty(world.Notices.Answers);
    }

    [UnitFact]
    public async Task GivenALogicallyDeletedInviter_WhenAccepting_ThenAcceptedAndNobodyIsTold()
    {
        // Given
        var (world, scope, inviter, invitee, invitation) = await OpenInvitationAsync();
        inviter.IsDeleted = true;

        // When
        var output = await Handler(world).HandleAsync(Command(invitee, invitation));

        // Then
        Assert.True(output.Success);
        Assert.Contains(invitee.ScopeOwnerships, ownership => ownership.ScopeId == scope.Id);
        Assert.Empty(world.Notices.Answers);
    }

    [UnitFact]
    public async Task GivenAnAnonymisedInviter_WhenAccepting_ThenAcceptedAndNobodyIsTold()
    {
        // Given — anonymisation clears the inviter; one not yet cleared is still not told
        var (world, scope, inviter, invitee, invitation) = await OpenInvitationAsync();
        inviter.AnonymisedAt = DateTime.UtcNow.AddMinutes(-1);

        // When
        var output = await Handler(world).HandleAsync(Command(invitee, invitation));

        // Then
        Assert.True(output.Success);
        Assert.Contains(invitee.ScopeOwnerships, ownership => ownership.ScopeId == scope.Id);
        Assert.Empty(world.Notices.Answers);
    }

    [UnitFact]
    public async Task GivenARestrictedInviter_WhenAccepting_ThenAcceptedAndNobodyIsTold()
    {
        // Given — NFR-24: a processing-restricted person is mailed nothing (AF-51c)
        var (world, scope, inviter, invitee, invitation) = await OpenInvitationAsync();
        inviter.ProcessingRestrictedAt = DateTime.UtcNow.AddMinutes(-1);

        // When
        var output = await Handler(world).HandleAsync(Command(invitee, invitation));

        // Then
        Assert.True(output.Success);
        Assert.Contains(invitee.ScopeOwnerships, ownership => ownership.ScopeId == scope.Id);
        Assert.Empty(world.Notices.Answers);
    }

    private sealed class FailingAuditLogWriter : IAuditLogWriter
    {
        public Task WriteAsync(string action, Guid? targetId, bool succeeded, string? failureReason) =>
            throw new InvalidOperationException("The audit store is unavailable.");
    }
}
