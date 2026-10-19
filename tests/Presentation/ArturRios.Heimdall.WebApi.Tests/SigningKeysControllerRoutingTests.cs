using System.Reflection;
using ArturRios.Heimdall.WebApi.Controllers;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for how GET /.well-known/jwks.json is wired: reachable by anyone, at the well-known path
// outside /api, under the credential-endpoint rate limit that bounds anonymous probing.
public class SigningKeysControllerRoutingTests
{
    private static MethodInfo KeySet => typeof(SigningKeysController).GetMethod(nameof(SigningKeysController.KeySet))!;

    [UnitFact]
    public void GivenTheKeySetAction_WhenInspected_ThenItIsAnonymousAndRateLimited()
    {
        // Given / When
        var limiting = KeySet.GetCustomAttribute<EnableRateLimitingAttribute>();

        // Then
        Assert.NotNull(KeySet.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(limiting);
        Assert.Equal("AuthAnonymous", limiting.PolicyName);
    }

    [UnitFact]
    public void GivenTheController_WhenRouted_ThenTheKeySetIsAtTheWellKnownPath()
    {
        // Given / When
        var prefix = typeof(SigningKeysController).GetCustomAttribute<RouteAttribute>()!.Template;
        var action = KeySet.GetCustomAttribute<HttpGetAttribute>()!.Template;

        // Then
        Assert.Equal(".well-known/jwks.json", $"{prefix}/{action}");
    }
}
