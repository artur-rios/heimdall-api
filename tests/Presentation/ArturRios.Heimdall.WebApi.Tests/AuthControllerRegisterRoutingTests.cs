using System.Reflection;
using ArturRios.Heimdall.WebApi.Controllers;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for how POST /api/auth/register is wired (UC-47): an anonymous caller must reach it, at
// the stated route, under the credential-endpoint rate limit that bounds address probing (FR-SR-04).
public class AuthControllerRegisterRoutingTests
{
    private static MethodInfo Register => typeof(AuthController).GetMethod(nameof(AuthController.Register))!;

    [UnitFact]
    public void GivenTheRegisterAction_WhenInspected_ThenItAllowsAnonymousCallers()
    {
        Assert.NotNull(Register.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [UnitFact]
    public void GivenTheRegisterAction_WhenInspected_ThenItIsAPostToRegister()
    {
        var post = Register.GetCustomAttribute<HttpPostAttribute>();

        Assert.NotNull(post);
        Assert.Equal("register", post.Template);
    }

    [UnitFact]
    public void GivenTheRegisterAction_WhenInspected_ThenItIsUnderTheAuthRateLimitPolicy()
    {
        var limiting = Register.GetCustomAttribute<EnableRateLimitingAttribute>();

        Assert.NotNull(limiting);
        Assert.Equal(Startup.AuthEndpointRateLimitPolicy, limiting.PolicyName);
    }
}
