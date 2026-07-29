using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.HealthChecks;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using ArturRios.Output;
using ArturRios.Util.Test.Functional;

namespace ArturRios.IdentityManager.WebApi.Tests;

// Joining the collection is what matters here: xUnit initializes the collection fixture before
// constructing any test class in it, so the container is running and migrated by the time the base
// constructor boots the API and reads the connection details the fixture publishes.
[Collection(nameof(FunctionalCollection))]
public class HealthCheckTests() : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string HealthCheckRoute = "/HealthCheck";
    private const string DetailedHealthCheckRoute = "/HealthCheck/detailed";

    // UC-30 liveness main flow (FR-HC-01) — public, no authentication.
    [FunctionalFact]
    public async Task GivenApiWorking_WhenHealthCheckEndpointCalled_ThenEndpointReturnsOk()
    {
        var output = await Gateway.GetAsync<DataOutput<string>>(HealthCheckRoute);

        Assert.Equal(HttpStatusCode.OK, output.StatusCode);
        Assert.NotNull(output.Body?.Data);
        Assert.Equal("Hello world!", output.Body.Data);
        Assert.Equal("Identity manager API is working.", output.Body.Messages.First());
    }

    // UC-30 detailed main flow (FR-HC-02/03/04/05) — SystemAdmin, database reachable.
    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenDetailedHealthCheckCalled_ThenReturnsHealthy()
    {
        // Given
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var output = await Gateway.GetAsync<DataOutput<HealthCheckOutput>>(DetailedHealthCheckRoute);

        // Then — the database is up (Testcontainers), so the aggregate and the Database service are Healthy
        Assert.Equal(HttpStatusCode.OK, output.StatusCode);
        Assert.NotNull(output.Body?.Data);
        Assert.Equal(HealthStatuses.Healthy, output.Body!.Data!.Status);

        var database = Assert.Single(output.Body.Data.Services);
        Assert.Equal("Database", database.Name);
        Assert.Equal(HealthStatuses.Healthy, database.Status);
    }

    // UC-30 AF-30a — authenticated but not a System Admin.
    [FunctionalFact]
    public async Task GivenNonSystemAdmin_WhenDetailedHealthCheckCalled_ThenForbidden()
    {
        // Given
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var output = await Gateway.GetAsync<DataOutput<HealthCheckOutput>>(DetailedHealthCheckRoute);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, output.StatusCode);
    }

    // UC-30 AF-30b — no/invalid authentication.
    [FunctionalFact]
    public async Task GivenNoToken_WhenDetailedHealthCheckCalled_ThenUnauthorized()
    {
        // Given no bearer token on the gateway

        // When
        var output = await Gateway.GetAsync<DataOutput<HealthCheckOutput>>(DetailedHealthCheckRoute);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, output.StatusCode);
    }
}
