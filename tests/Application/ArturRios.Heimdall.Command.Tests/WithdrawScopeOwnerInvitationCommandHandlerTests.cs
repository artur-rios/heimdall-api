using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for WithdrawScopeOwnerInvitationCommandHandler (UC-58, FR-SC-15): an owner or a System Admin ends
// an open invitation as withdrawn; an ended, expired or unknown one — or another scope's, named through this
// scope's route — is AF-58c; AF-58a and AF-58b come first; a withdrawal that loses a race is AF-58c too.
public class WithdrawScopeOwnerInvitationCommandHandlerTests
{
    private static IScopeOwnershipChecker Ownership(bool allowed)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);
        return checker.Object;
    }

    private static WithdrawScopeOwnerInvitationCommandHandler Handler(InvitationWorld world, bool allowed = true) =>
        new(world.Scopes, world.Invitations, world.Store, Ownership(allowed));

    private static WithdrawScopeOwnerInvitationCommand Command(Guid scopeId, Guid invitationId) => new()
    {
        ScopeId = scopeId, InvitationId = invitationId, ActingPersonId = Guid.NewGuid(), ActingRole = (int)Roles.ScopeAdmin
    };

    [UnitFact]
    public async Task GivenAnOpenInvitation_WhenAnOwnerWithdrawsIt_ThenItEndsWithdrawn()
    {
        // Given
        var world = new InvitationWorld();
        var scope = await world.ScopeAsync();
        var owner = await world.PersonAsync("Ada", owns: scope);
        var invitation = await world.InvitationAsync(scope, await world.PersonAsync("Grace"), owner);

        // When
        var output = await Handler(world).HandleAsync(Command(scope.PublicId, invitation.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Equal(invitation.PublicId, output.Data!.Id);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Withdrawn, invitation.Outcome);
        Assert.NotNull(invitation.EndedAt);
    }

    [UnitFact]
    public async Task GivenAnEndedInvitation_WhenWithdrawing_ThenItIsNotOpen()
    {
        // Given
        var world = new InvitationWorld();
        var scope = await world.ScopeAsync();
        var invitation = await world.InvitationAsync(scope, await world.PersonAsync("Grace"), null,
            endedAt: DateTime.UtcNow.AddMinutes(-1), outcome: ScopeOwnerInvitationOutcomes.Declined);

        // When
        var output = await Handler(world).HandleAsync(Command(scope.PublicId, invitation.PublicId));

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Declined, invitation.Outcome);
    }

    [UnitFact]
    public async Task GivenAnExpiredInvitation_WhenWithdrawing_ThenItIsNotOpen()
    {
        // Given
        var world = new InvitationWorld();
        var scope = await world.ScopeAsync();
        var invitation = await world.InvitationAsync(scope, await world.PersonAsync("Grace"), null,
            createdAt: DateTime.UtcNow.AddDays(-8), expiresAt: DateTime.UtcNow.AddDays(-1));

        // When
        var output = await Handler(world).HandleAsync(Command(scope.PublicId, invitation.PublicId));

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Null(invitation.EndedAt);
    }

    [UnitFact]
    public async Task GivenAnotherScopesInvitation_WhenWithdrawingThroughThisScope_ThenItIsNotOpenAndStaysOpen()
    {
        // Given — Review Focus 3: the caller owns this scope, the invitation is for another
        var world = new InvitationWorld();
        var scope = await world.ScopeAsync();
        var other = await world.ScopeAsync("Globex");
        var invitation = await world.InvitationAsync(other, await world.PersonAsync("Grace"), null);

        // When
        var output = await Handler(world).HandleAsync(Command(scope.PublicId, invitation.PublicId));

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Null(invitation.EndedAt);
    }

    [UnitFact]
    public async Task GivenACallerWhoMayNotManageTheScope_WhenWithdrawing_ThenNotScopeOwnerAndItStaysOpen()
    {
        // Given AF-58b
        var world = new InvitationWorld();
        var scope = await world.ScopeAsync();
        var invitation = await world.InvitationAsync(scope, await world.PersonAsync("Grace"), null);

        // When
        var output = await Handler(world, allowed: false).HandleAsync(Command(scope.PublicId, invitation.PublicId));

        // Then
        Assert.Equal([ScopeMessages.NotScopeOwner], output.Errors);
        Assert.Null(invitation.EndedAt);
    }

    [UnitFact]
    public async Task GivenALogicallyDeletedScope_WhenWithdrawing_ThenScopeNotFound()
    {
        // Given AF-58a
        var world = new InvitationWorld();
        var scope = await world.ScopeAsync(isDeleted: true);
        var invitation = await world.InvitationAsync(scope, await world.PersonAsync("Grace"), null);

        // When
        var output = await Handler(world).HandleAsync(Command(scope.PublicId, invitation.PublicId));

        // Then
        Assert.Equal([ScopeMessages.ScopeNotFound], output.Errors);
    }

    [UnitFact]
    public async Task GivenAnAcceptanceLandingBeforeTheWrite_WhenWithdrawing_ThenItIsNotOpenAndTheAcceptanceStands()
    {
        // Given — the invitee accepts between the handler's read and its write
        var world = new InvitationWorld(beforeConditionalWrite: racing =>
        {
            racing.EndedAt = DateTime.UtcNow;
            racing.Outcome = ScopeOwnerInvitationOutcomes.Accepted;
        });
        var scope = await world.ScopeAsync();
        var invitation = await world.InvitationAsync(scope, await world.PersonAsync("Grace"), null);

        // When
        var output = await Handler(world).HandleAsync(Command(scope.PublicId, invitation.PublicId));

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Accepted, invitation.Outcome);
    }
}
