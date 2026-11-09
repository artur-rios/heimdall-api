using System.Net;
using System.Text.Json;
using ArturRios.Heimdall.WebApi.Email;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace ArturRios.Heimdall.WebApi.Tests;

// UC-05: Heimdall's IEmailService, backed by Hermes. Reporting a refusal in the envelope and letting a failure to
// reach Hermes throw is the contract every sender was written against, so their failure handling stays as it was.
public class HermesEmailServiceTests
{
    private const string BaseUrl = "http://hermes-api.test:8080";
    private const string ApiKey = "heimdall-hermes-key";
    private const string Recipient = "person@test.local";
    private const string Subject = "Reset your password";
    private const string Body = "Follow this link: https://example.com/reset?token=secret";

    private sealed class StubHandler(HttpStatusCode status, Exception? failure = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Content { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Calls++;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Content = await request.Content!.ReadAsStringAsync(cancellationToken);

            return failure is not null
                ? throw failure
                : new HttpResponseMessage(status) { Content = new StringContent($"Hermes detail about {Recipient}") };
        }
    }

    private static HermesEmailService Service(HttpMessageHandler handler, string baseUrl = BaseUrl) =>
        new(new HttpClient(handler), new EmailDeliveryOptions { HermesBaseUrl = baseUrl, HermesApiKey = ApiKey });

    [UnitFact]
    public async Task GivenHermesAccepts_WhenSending_ThenTheOutputIsASuccess()
    {
        var output = await Service(new StubHandler(HttpStatusCode.Accepted)).SendEmailAsync(Recipient, Subject, Body);

        Assert.True(output.Success);
    }

    [UnitFact]
    public async Task GivenAMessage_WhenSending_ThenItCallsHermesWithItsKey()
    {
        var handler = new StubHandler(HttpStatusCode.Accepted);

        await Service(handler).SendEmailAsync(Recipient, Subject, Body);

        Assert.Equal($"{BaseUrl}/api/emails", handler.Uri?.ToString());
        Assert.Equal($"Bearer {ApiKey}", handler.Authorization);
    }

    [UnitFact]
    public async Task GivenABaseUrlWithATrailingSlash_WhenSending_ThenThePathIsStillRight()
    {
        var handler = new StubHandler(HttpStatusCode.Accepted);

        await Service(handler, $"{BaseUrl}/").SendEmailAsync(Recipient, Subject, Body);

        Assert.Equal($"{BaseUrl}/api/emails", handler.Uri?.ToString());
    }

    [UnitFact]
    public async Task GivenAMessage_WhenSending_ThenTheBodyHoldsExactlyTheRecipientSubjectAndBody()
    {
        var handler = new StubHandler(HttpStatusCode.Accepted);

        await Service(handler).SendEmailAsync(Recipient, Subject, Body);

        using var sent = JsonDocument.Parse(handler.Content!);
        var fields = sent.RootElement.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.GetString());

        Assert.Equal(new Dictionary<string, string?> { ["to"] = Recipient, ["subject"] = Subject, ["body"] = Body }, fields);
    }

    [UnitTheory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task GivenHermesDoesNotAccept_WhenSending_ThenTheOutputCarriesTheStatusAndOnlyOneAttemptIsMade(HttpStatusCode status)
    {
        var handler = new StubHandler(status);

        var output = await Service(handler).SendEmailAsync(Recipient, Subject, Body);

        Assert.False(output.Success);
        Assert.Contains(output.Errors, error => error.Contains(((int)status).ToString()));
        Assert.Equal(1, handler.Calls);
    }

    [UnitFact]
    public async Task GivenHermesRefuses_WhenSending_ThenTheOutputHoldsNoContentOrAddress()
    {
        var output = await Service(new StubHandler(HttpStatusCode.BadGateway)).SendEmailAsync(Recipient, Subject, Body);

        var errors = string.Join(' ', output.Errors);
        Assert.DoesNotContain(Recipient, errors);
        Assert.DoesNotContain("token=secret", errors);
    }

    [UnitFact]
    public async Task GivenHermesCannotBeReached_WhenSending_ThenTheFailureIsThrownOnceForTheSenderToHandle()
    {
        var handler = new StubHandler(HttpStatusCode.Accepted, new HttpRequestException("unreachable"));

        await Assert.ThrowsAsync<HttpRequestException>(() => Service(handler).SendEmailAsync(Recipient, Subject, Body));

        Assert.Equal(1, handler.Calls);
    }

    [UnitFact]
    public async Task GivenACanceledToken_WhenSending_ThenItThrowsAndCallsNothing()
    {
        var handler = new StubHandler(HttpStatusCode.Accepted);
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(handler).SendEmailAsync(Recipient, Subject, Body, source.Token));

        Assert.Equal(0, handler.Calls);
    }

    [FunctionalFact]
    public async Task GivenARealServer_WhenSending_ThenTheRequestArrivesAsHermesExpectsIt()
    {
        string? authorization = null;
        string? contentType = null;
        string? content = null;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapPost("/api/emails", async (HttpContext context) =>
        {
            authorization = context.Request.Headers.Authorization;
            contentType = context.Request.ContentType;
            using var reader = new StreamReader(context.Request.Body);
            content = await reader.ReadToEndAsync();

            return Results.StatusCode(StatusCodes.Status202Accepted);
        });
        await server.StartAsync();

        using var client = new HttpClient();
        var service = new HermesEmailService(
            client, new EmailDeliveryOptions { HermesBaseUrl = server.Urls.First(), HermesApiKey = ApiKey });

        var output = await service.SendEmailAsync(Recipient, Subject, Body);

        Assert.True(output.Success);
        Assert.Equal($"Bearer {ApiKey}", authorization);
        Assert.StartsWith("application/json", contentType);
        Assert.Contains(Recipient, content);
    }
}
