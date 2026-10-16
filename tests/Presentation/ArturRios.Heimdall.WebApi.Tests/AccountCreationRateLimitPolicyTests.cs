using System.Reflection;
using ArturRios.Heimdall.WebApi.Controllers;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.RateLimiting;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for how the three endpoints that create a person with a password are wired: each derives
// an Argon2id hash, so each is under the per-actor account-creation limit (Threat Model TH-03). The
// limit itself is exercised against the real limiter in AccountCreationRateLimitTests.
public class AccountCreationRateLimitPolicyTests
{
    private static void AssertUnderTheAccountCreationPolicy(MethodInfo action)
    {
        var limiting = action.GetCustomAttribute<EnableRateLimitingAttribute>();

        Assert.NotNull(limiting);
        Assert.Equal(Startup.AccountCreationRateLimitPolicy, limiting.PolicyName);
    }

    [UnitFact]
    public void GivenThePersonCreationActions_WhenInspected_ThenEachIsUnderTheAccountCreationPolicy()
    {
        // Given / When
        string[] actions =
        [
            nameof(PersonController.CreateAdmin),
            nameof(PersonController.CreateUser),
            nameof(PersonController.CreateScopeOwner)
        ];

        // Then
        foreach (var action in actions)
        {
            AssertUnderTheAccountCreationPolicy(typeof(PersonController).GetMethod(action)!);
        }
    }
}
