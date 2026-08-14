using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for the half of NFR-13 this application actually decides.
//
// NFR-13 requires a Google ID token to be checked for signature, issuer, audience and expiry before
// its claims are trusted. Three of those are Google's library's, run against certificates fetched
// from Google, and no test here can reach them without contacting Google or holding Google's signing
// key — that half stays verified by inspection, and the Testing Specification says so (§11.2).
//
// The audience is ours. It is the check that decides whether a token minted for somebody else's
// OAuth client is accepted by this one, and Google's contract is that a null Audience means "do not
// check the audience". So the difference between enforcing NFR-13's audience clause and silently
// dropping it is one assignment — and until the settings were split out, no test could tell which
// one was there.
public class GoogleIdTokenValidationSettingsTests
{
    private static GoogleSignInOptions Configured(params string[] clientIds) =>
        new() { ClientIds = clientIds };

    [UnitFact]
    public void GivenAConfiguredClient_WhenBuildingSettings_ThenItIsTheTrustedAudience()
    {
        var settings = GoogleIdTokenValidationSettings.For(Configured("111.apps.googleusercontent.com"));

        Assert.Equal(["111.apps.googleusercontent.com"], settings.Audience);
    }

    [UnitFact]
    public void GivenSeveralConfiguredClients_WhenBuildingSettings_ThenAllOfThemAreTrusted()
    {
        // A deployment serving several front ends lists them all, and Google issues one token per
        // client — so dropping any of them would refuse a caller the deployment does trust.
        var settings = GoogleIdTokenValidationSettings.For(
            Configured("111.apps.googleusercontent.com", "222.apps.googleusercontent.com"));

        Assert.Equal(
            ["111.apps.googleusercontent.com", "222.apps.googleusercontent.com"],
            settings.Audience);
    }

    [UnitFact]
    public void GivenAnyConfiguration_WhenBuildingSettings_ThenTheAudienceIsNeverNull()
    {
        // The regression this file exists for. Google reads a null Audience as "suppress audience
        // validation", so a change that stopped passing the client IDs would not fail loudly — it
        // would start accepting any token Google ever signed, for any application, and every other
        // test here would still pass.
        Assert.NotNull(GoogleIdTokenValidationSettings.For(Configured("111.apps.googleusercontent.com")).Audience);
        Assert.NotNull(GoogleIdTokenValidationSettings.For(Configured()).Audience);
    }

    [UnitFact]
    public void GivenDefaultSettings_WhenBuildingSettings_ThenNoOtherCheckIsRelaxed()
    {
        // Nothing here may loosen a check NFR-13 requires. The clock tolerances are the settings that
        // could quietly widen the window an expired token stays acceptable in; the library's own
        // default is 30 seconds, which is a clock-skew allowance rather than a grace period, and this
        // asserts a bound rather than that exact value so a library bump adjusting its own default
        // does not fail the build while a deliberate widening still would.
        var settings = GoogleIdTokenValidationSettings.For(Configured("111.apps.googleusercontent.com"));

        Assert.True(
            settings.ExpirationTimeClockTolerance <= TimeSpan.FromMinutes(1),
            $"expiry tolerance was widened to {settings.ExpirationTimeClockTolerance}");
        Assert.True(
            settings.IssuedAtClockTolerance <= TimeSpan.FromMinutes(1),
            $"issued-at tolerance was widened to {settings.IssuedAtClockTolerance}");

        // Not this application's rule to impose: a hosted-domain filter would refuse every caller
        // outside one GSuite domain, which UC-25 does not ask for.
        Assert.Null(settings.HostedDomain);
    }
}
