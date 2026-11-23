using System.Text.RegularExpressions;
using System.Xml.Linq;
using ArturRios.Heimdall.WebApi.Binding;
using ArturRios.Heimdall.WebApi.Documentation;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     Holds the document to the answers outside the API's own code: Kestrel's bodiless 500, the
///     production proxy's (Traefik's) 429 on the credential routes and its 500, 502 and 504, and the
///     answers no operation can list, which the document's description states instead. And guards
///     the audit: a new pipeline layer or MVC filter fails here until its statuses are documented.
/// </summary>
/// <remarks>
///     Traefik's configuration lives in yggdrasil (<c>stacks/heimdall-api.proxy.yml</c>), which this
///     repository cannot read in CI. The router rule is restated here as the filter restates it: a
///     path matching <c>(?i)^/api/auth/</c> and a method other than GET, HEAD and OPTIONS.
/// </remarks>
public partial class OpenApiResponseContractTests
{
    private static readonly string[] s_proxyUnlimited = ["GET", "HEAD", "OPTIONS"];

    private static readonly string[] s_gatewayStatuses = ["502", "504"];

    // The MVC filters the API adds to every action, each with the source ErrorResponseFilter documents
    // its answers under. A filter added without its statuses fails the audit below.
    private static readonly Dictionary<Type, ErrorSource[]> s_auditedFilters = new()
    {
        [typeof(MfaPendingGuardFilter)] = [ErrorSource.Authentication],
        [typeof(SubjectRightsOnlyGuardFilter)] = [ErrorSource.SubjectRights],
        [typeof(ActorLivenessFilter)] = [ErrorSource.Authentication],
        [typeof(PasswordHashSaturationFilter)] = [ErrorSource.PasswordHash],
        // Answers nothing: it logs a 5xx something else answered.
        [typeof(ServerErrorLoggingFilter)] = [],
        [typeof(MalformedRequestFilter)] = [ErrorSource.Malformed],
        // MVC's own, added by AddControllers: a body no input formatter reads is a 415, no body.
        [typeof(Microsoft.AspNetCore.Mvc.ModelBinding.UnsupportedContentTypeFilter)] = [ErrorSource.UnsupportedContentType]
    };

    // The middleware the API adds around Util.WebApi's standard pipeline, read from the source. Each
    // was audited: the metrics branch answers only on the metrics port; the developer exception page
    // only where it is on (stated in the description); forwarded headers, the rewrite and the rate
    // limiter (documented as RateLimit) answer nothing else; HTTPS redirection redirects only when it
    // knows an HTTPS port, which no deployment gives it.
    private static readonly string[] s_auditedMiddleware =
    [
        "UseDeveloperExceptionPage", "UseHeimdallMetrics", "UseHttpsRedirection", "UseRateLimiter", "UseRewriter",
        "UseTrustedForwardedHeaders"
    ];

    // The release of Util.WebApi whose standard pipeline was audited: forwarded headers,
    // TraceActivityMiddleware, ExceptionMiddleware (500 and Kestrel's body refusals, RequestBody and
    // Exception), Swagger (its own paths only), CORS (no status of its own on a real request) and
    // AuthenticationMiddleware (Authentication). Another release needs its pipeline audited again.
    private const string AuditedUtilWebApiVersion = "6.0.2";

    [GeneratedRegex(@"\bapp\.(Use\w+|Map\w*|Run)\(")]
    private static partial Regex PipelineCall();

    [GeneratedRegex("^/api/auth/", RegexOptions.IgnoreCase)]
    private static partial Regex ProxyLimitedPath();

    [UnitFact]
    public void GivenTheProxysCredentialRouter_WhenDocumentRead_ThenExactlyItsOperationsListTraefiks429()
    {
        using var document = LoadDocument();

        var routed = Operations(document)
            .Where(operation => ProxyLimitedPath().IsMatch(operation.Route) && !s_proxyUnlimited.Contains(operation.Method))
            .Select(operation => operation.OperationId)
            .Order(StringComparer.Ordinal)
            .ToList();
        var listing = Operations(document)
            .Where(operation => operation.Operation.GetProperty("responses").TryGetProperty("429", out var response)
                                && MediaTypes(response).Contains("text/plain"))
            .Select(operation => operation.OperationId)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Contains("Auth_Login", routed);
        Assert.DoesNotContain("Auth_GetTwoFactorStatus", routed);
        Assert.Equal(routed, listing);

        Assert.All(Operations(document).Where(operation => routed.Contains(operation.OperationId)), operation =>
        {
            var response = operation.Operation.GetProperty("responses").GetProperty("429");
            var headers = response.GetProperty("headers");

            Assert.Equal("string", response.GetProperty("content").GetProperty("text/plain").GetProperty("schema").GetProperty("type").GetString());
            Assert.True(headers.TryGetProperty("Retry-After", out _), operation.OperationId);
            Assert.True(headers.TryGetProperty("X-Retry-In", out _), operation.OperationId);
        });
    }

    [UnitFact]
    public void GivenOperationsReadingABody_WhenDocumentRead_ThenExactlyTheyListMvcs415WithoutABody()
    {
        using var document = LoadDocument();

        var reading = Operations(document)
            .Where(operation => operation.Operation.TryGetProperty("requestBody", out _))
            .Select(operation => operation.OperationId)
            .Order(StringComparer.Ordinal)
            .ToList();
        var listing = Operations(document)
            .Where(operation => operation.Operation.GetProperty("responses").TryGetProperty("415", out _))
            .Select(operation => operation.OperationId)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(reading);
        Assert.Equal(reading, listing);
        Assert.All(
            Operations(document).Where(operation => reading.Contains(operation.OperationId)),
            operation => Assert.Empty(MediaTypes(operation.Operation.GetProperty("responses").GetProperty("415"))));
    }

    [UnitFact]
    public void GivenEveryOperation_WhenRead_ThenItListsTraefiksGatewayAnswersAndKestrelsBodiless500()
    {
        using var document = LoadDocument();

        Assert.All(Operations(document), operation =>
        {
            var responses = operation.Operation.GetProperty("responses");

            foreach (var status in s_gatewayStatuses.Select(status => responses.GetProperty(status)))
            {
                Assert.Equal(["text/plain"], MediaTypes(status));
            }

            // The exception middleware's envelope, Traefik's text, and Kestrel's empty body, which a
            // response's content cannot express, so its description says so.
            var serverError = responses.GetProperty("500");

            Assert.Equal(["application/json", "text/plain"], MediaTypes(serverError));
            Assert.Contains("with no body", serverError.GetProperty("description").GetString(), StringComparison.Ordinal);
        });
    }

    [UnitFact]
    public void GivenTheDocument_WhenItsDescriptionIsRead_ThenItStatesTheAnswersNoOperationLists()
    {
        using var document = LoadDocument();

        var description = document.RootElement.GetProperty("info").GetProperty("description").GetString()!;

        Assert.Contains(SwaggerConfiguration.ErrorsDescription, description, StringComparison.Ordinal);

        string[] stated =
        [
            "upper bound", "`405`", "404 page not found", "`431`", "`501`", "`301` or `308`", "Cloudflare",
            "HTTP/2 stream reset", "keep-alive", "developer exception page"
        ];

        Assert.All(stated, phrase => Assert.Contains(phrase, description, StringComparison.Ordinal));
    }

    [UnitFact]
    public void GivenTheApisGlobalMvcFilters_WhenListed_ThenEachIsAnAuditedSource()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddHeimdallControllers();

        using var provider = services.BuildServiceProvider();

        var filters = provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters
            .Select(filter => filter is TypeFilterAttribute typed ? typed.ImplementationType
                : filter is ServiceFilterAttribute service ? service.ServiceType
                : filter.GetType())
            .ToList();

        Assert.NotEmpty(filters);
        Assert.All(filters, filter => Assert.True(
            s_auditedFilters.ContainsKey(filter),
            $"{filter.Name} is a global MVC filter nobody audited: document what it answers in ErrorResponseFilter, then list it here"));
    }

    [UnitFact]
    public void GivenTheEdgeAndStartupPipelines_WhenTheirMiddlewareIsRead_ThenEachIsAnAuditedLayer()
    {
        var webApi = Repository.PathTo("src", "Presentation", "ArturRios.Heimdall.WebApi");
        string[] sources = [Path.Combine(webApi, "Registration", "EdgePipeline.cs"), Path.Combine(webApi, "Startup.cs")];

        var calls = sources
            .Select(path => LineComment().Replace(BlockComment().Replace(File.ReadAllText(path), string.Empty), string.Empty))
            .SelectMany(code => PipelineCall().Matches(code).Select(match => match.Groups[1].Value))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(s_auditedMiddleware, calls);

        var packages = XDocument.Load(Repository.PathTo("Directory.Packages.props"));
        var version = packages.Descendants("PackageVersion")
            .Single(package => (string?)package.Attribute("Include") == "ArturRios.Util.WebApi")
            .Attribute("Version")?.Value;

        Assert.Equal(AuditedUtilWebApiVersion, version);
    }

    [UnitFact]
    public void GivenEveryDocumentedResponse_WhenItsDescriptionIsRead_ThenItNamesItsStatus()
    {
        using var document = LoadDocument();

        // A status ErrorResponseFilter has no reason phrase for falls back to the bare number: a status
        // a new source brought in without being described.
        var bare = Operations(document)
            .SelectMany(operation => operation.Operation.GetProperty("responses").EnumerateObject()
                .Where(response => response.Value.GetProperty("description").GetString()!
                    .StartsWith($"{response.Name}.", StringComparison.Ordinal))
                .Select(response => $"{operation.OperationId} {response.Name}"))
            .ToList();

        Assert.Empty(bare);
    }
}
