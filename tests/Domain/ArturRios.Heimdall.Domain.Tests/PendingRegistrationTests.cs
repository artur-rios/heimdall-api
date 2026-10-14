using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Domain.Tests;

// Unit tests for PendingRegistration's administrative reference (UC-49, UC-50) and its guess budget
// (FR-SR-09), now that both are read outside the confirmer.
public class PendingRegistrationTests
{
    [UnitFact]
    public void GivenTwoNewRegistrations_WhenCreated_ThenEachHasItsOwnPublicId()
    {
        // Given / When
        var first = new PendingRegistration();
        var second = new PendingRegistration();

        // Then — random, so it says nothing about the registrant or how many registrations exist
        Assert.NotEqual(Guid.Empty, first.PublicId);
        Assert.NotEqual(first.PublicId, second.PublicId);
    }

    [UnitFact]
    public void GivenTheGuessBudget_WhenRead_ThenItIsTheLoginLockoutThreshold()
    {
        // Given / When / Then — FR-SR-09: ten wrong passwords discard the registration
        Assert.Equal(10, PendingRegistration.MaxConfirmationGuesses);
    }
}
