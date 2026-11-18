using System.Net;
using System.Text;
using System.Text.Json;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     A request body over the server's limit, sent to a real Kestrel: the 413 it is refused with is
///     the status, media type and body the published document gives it.
/// </summary>
/// <remarks>
///     <para>
///         The in-memory test server enforces no body size, so this host runs on Kestrel
///         (<c>WebApplicationFactory.UseKestrel</c>). Its limit is lowered to a kilobyte, so the test
///         sends a few kilobytes rather than the default limit's thirty megabytes: what is checked is
///         the path a body over the limit takes, which is the same at any limit. Kestrel refuses it as
///         the action's body binding starts to read it, and ExceptionMiddleware answers.
///     </para>
///     <para>
///         <c>Expect: 100-continue</c> keeps the client from sending the body: Kestrel would only ask for
///         it once the read started, and the read is what refuses it. A body sent anyway would be left
///         unread when Kestrel closes the connection, and the reset that follows can cost the client
///         the answer.
///     </para>
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class KestrelRequestBodyLimitTests
{
    private const string Route = "/api/auth/password-recovery";
    private const long BodyLimit = 1024;

    [FunctionalFact]
    public async Task GivenABodyOverTheServersLimit_WhenItIsPosted_ThenThe413IsTheDocumentedBody()
    {
        var savedEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        HttpStatusCode status;
        string? mediaType;
        string body;

        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "local");

            // Given a server whose request bodies are limited to a kilobyte
            await using var factory = new KestrelHost();
            factory.UseKestrel(0);
            factory.UseKestrel(options => options.Limits.MaxRequestBodySize = BodyLimit);

            using var client = factory.CreateClient();
            var json = JsonSerializer.Serialize(new { email = new string('a', 4 * (int)BodyLimit) + "@example.com" });
            using var request = new HttpRequestMessage(HttpMethod.Post, Route)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.ExpectContinue = true;

            // When
            using var response = await client.SendAsync(request);

            status = response.StatusCode;
            mediaType = response.Content.Headers.ContentType?.MediaType;
            body = await response.Content.ReadAsStringAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", savedEnvironment);
        }

        // Then
        Assert.True(status == HttpStatusCode.RequestEntityTooLarge, $"Expected 413, got {(int)status}: {body}");

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Environment.GetEnvironmentVariable("HEIMDALL_OPENAPI_DOCUMENT")
            ?? Path.Combine(Repository.Root, "docs", "openapi", "heimdall.json")));
        using var envelope = JsonDocument.Parse(body);

        var content = document.RootElement.GetProperty("paths").GetProperty(Route).GetProperty("post")
            .GetProperty("responses").GetProperty("413").GetProperty("content");
        var schemaName = content.EnumerateObject().Single().Value.GetProperty("schema").GetProperty("$ref").GetString()!
            .Split('/')[^1];
        var properties = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(schemaName)
            .GetProperty("properties").EnumerateObject().Select(property => property.Name).ToHashSet();

        Assert.Equal(content.EnumerateObject().Single().Name, mediaType);
        Assert.All(envelope.RootElement.EnumerateObject(), member => Assert.Contains(member.Name, properties));
        Assert.Equal(string.Empty, envelope.RootElement.GetProperty("data").GetString());
        Assert.NotEmpty(envelope.RootElement.GetProperty("errors").EnumerateArray());
    }

    // Its own factory rather than WithWebHostBuilder's derived one, so UseKestrel configures the very
    // factory that builds the host and the client.
    private sealed class KestrelHost : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Local");
    }
}
