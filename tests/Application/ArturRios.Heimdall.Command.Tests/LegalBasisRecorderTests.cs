using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for LegalBasisRecorder (NFR-23). The property under test is that an identity can never
// be created without a basis recorded against it, and that the one basis the system cannot honour is
// never the one recorded.
public class LegalBasisRecorderTests
{
    private static Person NewPerson() => new() { PublicId = Guid.NewGuid(), Name = "Ada", Email = "a@t.local" };

    private static Scope ScopeDeclaring(LegalBases? basis) =>
        new() { PublicId = Guid.NewGuid(), Name = "Acme", DefaultLegalBasis = (int?)basis };

    [UnitFact]
    public void GivenNoScope_WhenRecording_ThenContractPerformanceApplies()
    {
        var person = NewPerson();
        var at = DateTime.UtcNow;

        LegalBasisRecorder.Record(person, scope: null, at);

        Assert.Equal((int)LegalBases.ContractPerformance, person.LegalBasis);
        Assert.Equal(LegalBasisRecorder.CurrentPrivacyNoticeVersion, person.PrivacyNoticeVersion);
        Assert.Equal(at, person.BasisRecordedAt);
    }

    [UnitFact]
    public void GivenAScopeDeclaringABasis_WhenRecording_ThenTheTenantsBasisIsUsed()
    {
        // The scope is the tenant boundary, and in most deployments the tenant is the controller for
        // the identities inside it
        var person = NewPerson();

        LegalBasisRecorder.Record(person, ScopeDeclaring(LegalBases.LegitimateInterests), DateTime.UtcNow);

        Assert.Equal((int)LegalBases.LegitimateInterests, person.LegalBasis);
    }

    [UnitFact]
    public void GivenAScopeDeclaringNothing_WhenRecording_ThenTheDefaultApplies()
    {
        var person = NewPerson();

        LegalBasisRecorder.Record(person, ScopeDeclaring(null), DateTime.UtcNow);

        Assert.Equal((int)LegalBases.ContractPerformance, person.LegalBasis);
    }

    [UnitFact]
    public void GivenAScopeSomehowDeclaringConsent_WhenRecording_ThenItIsNotHonoured()
    {
        // The validators refuse consent on the way in. This refuses it on the way out too: a guard
        // that exists in one direction only is one migration away from being bypassed, and consent
        // needs a withdrawal path that does not exist.
        var person = NewPerson();

        LegalBasisRecorder.Record(person, ScopeDeclaring(LegalBases.Consent), DateTime.UtcNow);

        Assert.NotEqual((int)LegalBases.Consent, person.LegalBasis);
        Assert.Equal((int)LegalBases.ContractPerformance, person.LegalBasis);
    }

    [UnitFact]
    public void GivenAScopeDeclaringUnrecorded_WhenRecording_ThenARealBasisIsUsed()
    {
        // Unrecorded describes rows that predate the mechanism. It is not something a new identity
        // may be created under, or the marker would stop meaning what it says.
        var person = NewPerson();

        LegalBasisRecorder.Record(person, ScopeDeclaring(LegalBases.Unrecorded), DateTime.UtcNow);

        Assert.Equal((int)LegalBases.ContractPerformance, person.LegalBasis);
    }

    [UnitFact]
    public void GivenAScopeDeclaringAValueOutsideTheEnum_WhenRecording_ThenTheDefaultApplies()
    {
        var person = NewPerson();

        LegalBasisRecorder.Record(person, new Scope { DefaultLegalBasis = 99 }, DateTime.UtcNow);

        Assert.Equal((int)LegalBases.ContractPerformance, person.LegalBasis);
    }

    [UnitFact]
    public void GivenAGoogleUser_WhenRecording_ThenItIsRecordedTheSameWay()
    {
        // UC-25 creates an identity with no administrator involved, so it is the path where an
        // unrecorded basis would be easiest to end up with and hardest to notice
        var googleUser = new GoogleUser { PublicId = Guid.NewGuid(), GoogleId = "1", Email = "a@t.local" };

        LegalBasisRecorder.Record(googleUser, ScopeDeclaring(LegalBases.LegitimateInterests), DateTime.UtcNow);

        Assert.Equal((int)LegalBases.LegitimateInterests, googleUser.LegalBasis);
        Assert.NotNull(googleUser.BasisRecordedAt);
    }
}
