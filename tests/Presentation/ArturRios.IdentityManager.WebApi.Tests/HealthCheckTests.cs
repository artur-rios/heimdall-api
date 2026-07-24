using System.Net;
using ArturRios.Configuration.Enums;
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

    [FunctionalFact]
    public async Task GivenApiWorking_WhenHealthCheckEndpointCalled_ThenEndpointReturnsOk()
    {
        var output = await Gateway.GetAsync<DataOutput<string>>(HealthCheckRoute);

        Assert.Equal(HttpStatusCode.OK, output.StatusCode);
        Assert.NotNull(output.Body?.Data);
        Assert.Equal("Hello world!", output.Body.Data);
        Assert.Equal("Identity manager API is working.", output.Body.Messages.First());
    }
}
