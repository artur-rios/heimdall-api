using ArturRios.Heimdall.Shared.Security;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Shared.Tests;

// Unit tests for TwoFactorLifetimes (FR-2F-03, NFR-17).
//
// There is one property here worth executing, and it is not either number on its own: that the two
// agree. They were once three constants in three files, and the one furthest from the other two
// disagreed with them — which left a login attempt with two deadlines, the earlier of which killed
// the challenge token five minutes before the code it was issued with expired.
//
// NonFunctionalRequirementTests asserts the same agreement end to end, on the artefacts a real login
// produces. This asserts it at the source, so the failure names the cause rather than a symptom of
// it, and so the suite still catches a reintroduction if the functional tests are ever skipped.
public class TwoFactorLifetimesTests
{
    [UnitFact]
    public void GivenTheTwoFactorLifetimes_WhenTheyAreCompared_ThenTheChallengeOutlastsItsEmailCode()
    {
        // A challenge token shorter than the code it carries is the defect; a longer one would be a
        // window NFR-17 did not ask for. Equality is the only relationship that is ever right.
        Assert.Equal(TwoFactorLifetimes.EmailCode, TwoFactorLifetimes.ChallengeToken);
    }

    [UnitFact]
    public void GivenFR2F03_WhenTheEmailCodeLifetimeIsRead_ThenItIsTheTenMinutesTheSpecificationPromises()
    {
        // The ten minutes is not incidental: it is the tolerance the specification chose for mail
        // delivery, the one part of this flow outside the system's control. Pinned so that lowering
        // it is a deliberate act with a failing test attached, rather than a quiet retraction of a
        // promise the Privacy-facing documents make.
        Assert.Equal(TimeSpan.FromMinutes(10), TwoFactorLifetimes.EmailCode);
    }
}
