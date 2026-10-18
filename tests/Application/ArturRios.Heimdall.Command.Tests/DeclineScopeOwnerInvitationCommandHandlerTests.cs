using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for DeclineScopeOwnerInvitationCommandHandler (UC-61, FR-SC-17): the invitee declining an open
// invitation ends it as declined and the inviter is told; an ended invitation, somebody else's and a decline
// that loses a race are AF-61a; an inviter who is gone is not told.
public class DeclineScopeOwnerInvitationCommandHandlerTests
{
    private static DeclineScopeOwnerInvitationCommandHandler Handler(InvitationWorld world) =>
        new(world.Persons, world.Scopes, world.Invitations, world.Store, world.Notices);

    private static DeclineScopeOwnerInvitationCommand Command(Person actor, ScopeOwnerInvitation invitation) => new()
    {
        InvitationId = invitation.PublicId, ActingPersonId = actor.PublicId, ActingRole = (int)Roles.ScopeAdmin
    };

    [UnitFact]
    public async Task GivenAnOpenInvitation_WhenTheInviteeDeclines_ThenItEndsDeclinedAndTheInviterIsTold()
    {
        // Given
        var world = new InvitationWorld();
        var scope = await world.ScopeAsync();
        var inviter = await world.PersonAsync("Ada", owns: scope);
        var invitee = await world.PersonAsync("Grace");
        var invitation = await world.InvitationAsync(scope, invitee, inviter);

        // When
        var output = await Handler(world).HandleAsync(Command(invitee, invitation));

        // Then
        Assert.True(output.Success);
        Assert.Contains(ScopeInvitationMessages.InvitationDeclined, output.Messages);
        Assert.Equal(invitation.PublicId, output.Data!.Id);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Declined, invitation.Outcome);
        Assert.Empty(invitee.ScopeOwnerships);
        Assert.Equal((inviter.Email, invitee.Name, scope.Name, false), Assert.Single(world.Notices.Answers));
    }

    [UnitFact]
    public async Task GivenAWithdrawnInvitation_WhenDeclining_ThenNotOpen()
    {
        // Given
        var world = new InvitationWorld();
        var scope = await world.ScopeAsync();
        var invitee = await world.PersonAsync("Grace");
        var invitation = await world.InvitationAsync(scope, invitee, null, endedAt: DateTime.UtcNow,
            outcome: ScopeOwnerInvitationOutcomes.Withdrawn);

        // When
        var output = await Handler(world).HandleAsync(Command(invitee, invitation));

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Withdrawn, invitation.Outcome);
    }

    [UnitFact]
    public async Task GivenSomebodyElsesInvitation_WhenDeclining_ThenNotOpenAndItStaysOpen()
    {
        // Given
        var world = new InvitationWorld();
        var scope = await world.ScopeAsync();
        var invitation = await world.InvitationAsync(scope, await world.PersonAsync("Grace"), null);
        var outsider = await world.PersonAsync("Alan");

        // When
        var output = await Handler(world).HandleAsync(Command(outsider, invitation));

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Null(invitation.EndedAt);
    }

    [UnitFact]
    public async Task GivenAWithdrawalLandingBeforeTheWrite_WhenDeclining_ThenNotOpenAndNobodyIsTold()
    {
        // Given — an owner withdraws after the handler read the invitation
        var world = new InvitationWorld(racing =>
        {
            racing.EndedAt = DateTime.UtcNow;
            racing.Outcome = ScopeOwnerInvitationOutcomes.Withdrawn;
        });
        var scope = await world.ScopeAsync();
        var inviter = await world.PersonAsync("Ada", owns: scope);
        var invitee = await world.PersonAsync("Grace");
        var invitation = await world.InvitationAsync(scope, invitee, inviter);

        // When
        var output = await Handler(world).HandleAsync(Command(invitee, invitation));

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Withdrawn, invitation.Outcome);
        Assert.Empty(world.Notices.Answers);
    }

    [UnitFact]
    public async Task GivenAnInviterWhoseRowIsGone_WhenDeclining_ThenDeclinedAndNobodyIsTold()
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
        Assert.Equal(ScopeOwnerInvitationOutcomes.Declined, invitation.Outcome);
        Assert.Empty(world.Notices.Answers);
    }

    [UnitFact]
    public async Task GivenALogicallyDeletedInviter_WhenDeclining_ThenDeclinedAndNobodyIsTold()
    {
        // Given
        var world = new InvitationWorld();
        var scope = await world.ScopeAsync();
        var inviter = await world.PersonAsync("Ada", isDeleted: true, owns: scope);
        var invitee = await world.PersonAsync("Grace");
        var invitation = await world.InvitationAsync(scope, invitee, inviter);

        // When
        var output = await Handler(world).HandleAsync(Command(invitee, invitation));

        // Then
        Assert.True(output.Success);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Declined, invitation.Outcome);
        Assert.Empty(world.Notices.Answers);
    }
}
