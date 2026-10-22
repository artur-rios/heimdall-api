using System.Net;
using System.Text.Json;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     Through the real pipeline, a 5xx an action returns is logged once by <c>ServerErrorLoggingFilter</c>,
///     and an exception that escapes is logged once by the library's <c>ExceptionMiddleware</c> and not by
///     the filter (#227). Guards the filter's registration and the one-line-per-5xx claim.
/// </summary>
[Collection(nameof(FunctionalCollection))]
public class ServerErrorLoggingEndToEndTests
{
    private const string FilterSource = "ArturRios.Heimdall.WebApi.Security.ServerErrorLoggingFilter";
    private const string ExceptionSource = "ArturRios.Util.WebApi.Middleware.ExceptionMiddleware";

    [FunctionalFact]
    public async Task GivenAnActionAnswering500_WhenRequested_ThenTheFilterLogsExactlyOneErrorLine()
    {
        // Given / When
        var (status, entries) = await RequestAsync("/api/test-faults/result");

        // Then
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        var line = Assert.Single(entries, entry => Source(entry) == FilterSource);
        Assert.Equal("Error", line.GetProperty("Level").GetString());
        var properties = line.GetProperty("Properties");
        Assert.Equal("api/test-faults/result", properties.GetProperty("Route").GetString());
        Assert.Equal(500, properties.GetProperty("StatusCode").GetInt32());
        Assert.False(string.IsNullOrEmpty(properties.GetProperty("TraceId").GetString()));
        Assert.DoesNotContain(entries, entry => Source(entry) == ExceptionSource && Level(entry) == "Error");
    }

    [FunctionalFact]
    public async Task GivenAnActionThatThrows_WhenRequested_ThenExceptionMiddlewareLogsOneErrorAndTheFilterNone()
    {
        // Given / When
        var (status, entries) = await RequestAsync("/api/test-faults/throw");

        // Then
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        var line = Assert.Single(entries, entry => Source(entry) == ExceptionSource && Level(entry) == "Error");
        Assert.NotNull(line.GetProperty("Exception").GetString());
        Assert.DoesNotContain(entries, entry => Source(entry) == FilterSource);
    }

    private static string? Source(JsonElement entry) =>
        entry.TryGetProperty("Properties", out var properties) && properties.TryGetProperty("SourceContext", out var s)
            ? s.GetString()
            : null;

    private static string? Level(JsonElement entry) => entry.GetProperty("Level").GetString();

    private static async Task<(HttpStatusCode Status, List<JsonElement> Entries)> RequestAsync(string path)
    {
        // A host of its own, logging to a directory of its own so its lines can be read back; Startup
        // reads the variables once, while the host starts.
        var logDirectory = Directory.CreateTempSubdirectory("heimdall-5xx-").FullName;
        var savedLogger = Log.Logger;
        var saved = new[] { "ASPNETCORE_ENVIRONMENT", "HEIMDALL_LOG_DIRECTORY" }
            .ToDictionary(variable => variable, Environment.GetEnvironmentVariable);
        HttpStatusCode status;

        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "local");
            Environment.SetEnvironmentVariable("HEIMDALL_LOG_DIRECTORY", logDirectory);

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
                .UseEnvironment("Local")
                .ConfigureTestServices(services =>
                    services.AddControllers().AddApplicationPart(typeof(FaultController).Assembly)));

            using var client = factory.CreateClient();
            using var response = await client.GetAsync(path);
            status = response.StatusCode;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
            Log.Logger = savedLogger;

            foreach (var (variable, value) in saved)
            {
                Environment.SetEnvironmentVariable(variable, value);
            }
        }

        try
        {
            var lines = Directory.EnumerateFiles(logDirectory).SelectMany(File.ReadAllLines);

            return (status, [.. lines.Select(line => JsonDocument.Parse(line).RootElement.Clone())]);
        }
        finally
        {
            Directory.Delete(logDirectory, recursive: true);
        }
    }
}

/// <summary>Faults on demand for <see cref="ServerErrorLoggingEndToEndTests" />; added to the host by that test only.</summary>
[Route("api/test-faults")]
[AllowAnonymous]
public class FaultController : Controller
{
    [HttpGet("result")]
    public IActionResult Result() => StatusCode(StatusCodes.Status500InternalServerError);

    [HttpGet("throw")]
    public IActionResult Throw() => throw new InvalidOperationException("Injected action fault");
}
