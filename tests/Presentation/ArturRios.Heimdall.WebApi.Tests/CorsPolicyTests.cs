using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for the cross-origin policy Startup builds from HEIMDALL_CORS_ALLOWED_ORIGINS. A
// browser front end on another origin reads only the response headers the policy exposes, and
// Retry-After is not one it may read by default, so the policy has to name it for a 429's wait to
// reach that front end.
[Collection(nameof(FunctionalCollection))]
public class CorsPolicyTests
{
    private const string FrontEndOrigin = "http://localhost:8081";

    [FunctionalFact]
    public async Task GivenAConfiguredFrontEnd_WhenItCallsTheApi_ThenRetryAfterIsExposedToIt()
    {
        // Given — the origin is read once, while the host starts
        var savedEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var savedOrigins = Environment.GetEnvironmentVariable(PublicHostSettings.CorsAllowedOriginsVariable);
        HttpResponseMessage response;

        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "local");
            Environment.SetEnvironmentVariable(PublicHostSettings.CorsAllowedOriginsVariable, FrontEndOrigin);

            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.UseEnvironment("Local"));

            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/healthcheck");
            request.Headers.Add("Origin", FrontEndOrigin);

            // When
            response = await client.SendAsync(request);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", savedEnvironment);
            Environment.SetEnvironmentVariable(PublicHostSettings.CorsAllowedOriginsVariable, savedOrigins);
        }

        // Then
        using (response)
        {
            Assert.Equal([FrontEndOrigin], response.Headers.GetValues("Access-Control-Allow-Origin"));
            Assert.Contains(
                "Retry-After",
                response.Headers.GetValues("Access-Control-Expose-Headers")
                    .SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries)),
                StringComparer.OrdinalIgnoreCase);
        }
    }
}
