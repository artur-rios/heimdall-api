using System.Reflection;
using ArturRios.Heimdall.WebApi.Controllers;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.RateLimiting;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for how the two endpoints that began verifying a password are wired: PUT
// /api/persons/{id}, for an own email change (FR-PE-14), and POST /api/auth/2fa/enable (FR-2F-18).
// Every other password-verifying endpoint is under the credential-endpoint rate limit (NFR-18, Threat
// Model TH-32), and without it either would be the way around it: a stolen token could guess its
// owner's password there as fast as the password-hash gate allowed.
public class ReauthenticationRateLimitTests
{
    private static void AssertUnderTheAuthRateLimitPolicy(MethodInfo action)
    {
        var limiting = action.GetCustomAttribute<EnableRateLimitingAttribute>();

        Assert.NotNull(limiting);
        Assert.Equal(Startup.AuthEndpointRateLimitPolicy, limiting.PolicyName);
    }

    [UnitFact]
    public void GivenTheUpdatePersonAction_WhenInspected_ThenItIsUnderTheAuthRateLimitPolicy()
    {
        // Given / When
        var action = typeof(PersonController).GetMethod(nameof(PersonController.Update))!;

        // Then
        AssertUnderTheAuthRateLimitPolicy(action);
    }

    [UnitFact]
    public void GivenTheEnableTwoFactorAction_WhenInspected_ThenItIsUnderTheAuthRateLimitPolicy()
    {
        // Given / When
        var action = typeof(AuthController).GetMethod(nameof(AuthController.EnableTwoFactorAuth))!;

        // Then
        AssertUnderTheAuthRateLimitPolicy(action);
    }
}
