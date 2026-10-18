using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Domain.Tests;

// Unit tests for ScopeOwnerInvitation (UC-21, UC-58…UC-61): an invitation is open while it has not ended
// and its expiry is still ahead — the instant of expiry already counts as expired — the stored outcome
// values are the ones the table's check and the documentation name, and only a live, unrestricted,
// unanonymised administrator is an eligible invitee.
public class ScopeOwnerInvitationTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private static bool IsOpen(ScopeOwnerInvitation invitation) =>
        ScopeOwnerInvitation.OpenAt(Now).Compile()(invitation);

    private static bool IsEligible(Person person) => ScopeOwnerInvitation.EligibleInvitee.Compile()(person);

    private static Person Invitee(Roles role = Roles.ScopeAdmin) => new() { RoleId = (long)role };

    private static ScopeOwnerInvitation Invitation(DateTime expiresAt, DateTime? endedAt = null,
        ScopeOwnerInvitationOutcomes? outcome = null) => new()
    {
        ScopeId = 1, InviteePersonId = 2, CreatedAt = Now.AddDays(-1), ExpiresAt = expiresAt,
        EndedAt = endedAt, Outcome = outcome
    };

    [UnitFact]
    public void GivenAnUnendedInvitationExpiringLater_WhenChecked_ThenItIsOpen()
    {
        // Given
        var invitation = Invitation(Now.AddSeconds(1));

        // When
        var open = IsOpen(invitation);

        // Then
        Assert.True(open);
    }

    [UnitFact]
    public void GivenAnInvitationExpiringNow_WhenChecked_ThenItIsNotOpen()
    {
        // Given
        var invitation = Invitation(Now);

        // When
        var open = IsOpen(invitation);

        // Then
        Assert.False(open);
    }

    [UnitFact]
    public void GivenAnEndedInvitation_WhenChecked_ThenItIsNotOpen()
    {
        // Given
        var invitation = Invitation(Now.AddDays(1), Now.AddMinutes(-1), ScopeOwnerInvitationOutcomes.Withdrawn);

        // When
        var open = IsOpen(invitation);

        // Then
        Assert.False(open);
    }

    [UnitFact]
    public void GivenTheOutcomes_WhenStored_ThenTheyAreOneToFour()
    {
        // Given
        // 1 accepted, 2 declined, 3 withdrawn, 4 voided

        // When
        var stored = new[]
        {
            (short)ScopeOwnerInvitationOutcomes.Accepted, (short)ScopeOwnerInvitationOutcomes.Declined,
            (short)ScopeOwnerInvitationOutcomes.Withdrawn, (short)ScopeOwnerInvitationOutcomes.Voided
        };

        // Then
        Assert.Equal(new short[] { 1, 2, 3, 4 }, stored);
    }

    [UnitFact]
    public void GivenTwoNewInvitations_WhenCreated_ThenEachHasItsOwnPublicId()
    {
        // Given
        // Two invitations, nothing set

        // When
        var first = new ScopeOwnerInvitation();
        var second = new ScopeOwnerInvitation();

        // Then
        Assert.NotEqual(Guid.Empty, first.PublicId);
        Assert.NotEqual(first.PublicId, second.PublicId);
    }

    [UnitTheory]
    [InlineData(Roles.ScopeAdmin)]
    [InlineData(Roles.SystemAdmin)]
    public void GivenALiveUnrestrictedAdministrator_WhenCheckedAsInvitee_ThenEligible(Roles role)
    {
        // Given
        var person = Invitee(role);

        // When
        var eligible = IsEligible(person);

        // Then
        Assert.True(eligible);
    }

    [UnitFact]
    public void GivenAUser_WhenCheckedAsInvitee_ThenNotEligible()
    {
        // Given
        var person = Invitee(Roles.User);

        // When
        var eligible = IsEligible(person);

        // Then
        Assert.False(eligible);
    }

    [UnitFact]
    public void GivenALogicallyDeletedAdministrator_WhenCheckedAsInvitee_ThenNotEligible()
    {
        // Given
        var person = Invitee();
        person.IsDeleted = true;

        // When
        var eligible = IsEligible(person);

        // Then
        Assert.False(eligible);
    }

    [UnitFact]
    public void GivenAProcessingRestrictedAdministrator_WhenCheckedAsInvitee_ThenNotEligible()
    {
        // Given
        var person = Invitee();
        person.ProcessingRestrictedAt = Now;

        // When
        var eligible = IsEligible(person);

        // Then
        Assert.False(eligible);
    }

    [UnitFact]
    public void GivenAnAnonymisedAdministrator_WhenCheckedAsInvitee_ThenNotEligible()
    {
        // Given
        var person = Invitee();
        person.AnonymisedAt = Now;

        // When
        var eligible = IsEligible(person);

        // Then
        Assert.False(eligible);
    }
}
