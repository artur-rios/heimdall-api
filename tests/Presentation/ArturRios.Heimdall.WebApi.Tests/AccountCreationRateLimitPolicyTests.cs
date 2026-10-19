using System.Reflection;
using ArturRios.Heimdall.WebApi.Controllers;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.RateLimiting;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for how the four endpoints that create a person with a password, or invite one and mail
// them, are wired: each derives an Argon2id hash or mails a third party, so each is under the per-actor
// account-creation limit (Threat Model TH-03). The limit itself is exercised against the real limiter
// in AccountCreationRateLimitTests.
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
        // Given
        string[] actions =
        [
            nameof(PersonController.CreateAdmin),
            nameof(PersonController.CreateUser),
            nameof(PersonController.CreateScopeOwner),
            nameof(PersonController.AddScopeOwner)
        ];

        // When
        var methods = actions.Select(action => typeof(PersonController).GetMethod(action)!);

        // Then
        foreach (var method in methods)
        {
            AssertUnderTheAccountCreationPolicy(method);
        }
    }
}
