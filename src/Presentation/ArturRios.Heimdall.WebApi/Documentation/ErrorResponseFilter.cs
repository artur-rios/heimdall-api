using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Mime;
using System.Reflection;
using System.Text.Json;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Output;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ArturRios.Heimdall.WebApi.Documentation;

/// <summary>What answers an error status on an operation, in the order its description names them.</summary>
public enum ErrorSource
{
    /// <summary><c>MalformedRequestFilter</c>: a body or query MVC could not bind. <c>DataOutput&lt;object?&gt;</c>.</summary>
    Malformed,

    /// <summary>The handler, through the status map its action passes. The action's envelope, <c>data</c> null.</summary>
    Handler,

    /// <summary><c>AuthenticationMiddleware</c>, <c>MfaPendingGuardFilter</c>, <c>ActorLivenessFilter</c>: <c>ProcessOutput</c>.</summary>
    Authentication,

    /// <summary><c>RoleRequirementFilter</c>: <c>ProcessOutput</c>.</summary>
    Role,

    /// <summary><c>SubjectRightsOnlyGuardFilter</c>: <c>ProcessOutput</c>.</summary>
    SubjectRights,

    /// <summary>The rate limiter's <c>OnRejected</c>: <c>DataOutput&lt;object?&gt;</c>, <c>application/json</c> only.</summary>
    RateLimit,

    /// <summary><c>ExceptionMiddleware</c>: <c>DataOutput&lt;string&gt;</c> with an empty <c>data</c>, <c>application/json</c> only.</summary>
    Exception,

    /// <summary><c>PasswordHashSaturationFilter</c>: <c>ProcessOutput</c> and <c>Retry-After: 5</c>.</summary>
    PasswordHash,

    /// <summary>
    ///     Kestrel refusing the request body while the action's body binding reads it: a
    ///     <c>BadHttpRequestException</c> that <c>ExceptionMiddleware</c> answers with its own status and
    ///     <c>DataOutput&lt;string&gt;</c>, an empty <c>data</c> and the reason phrase as the error,
    ///     <c>application/json</c> only.
    /// </summary>
    RequestBody,

    /// <summary>
    ///     Kestrel refusing the request before the API sees it, while it parses the request line and
    ///     headers: its own response, with <c>Content-Length: 0</c> and no body.
    /// </summary>
    Server
}

/// <summary>
///     The error statuses each operation's handler can answer, as scripts/error_responses.py derives
///     them from the source: <c>Documentation/error-responses.json</c>, embedded in this assembly.
/// </summary>
/// <remarks>
///     Which messages a handler fails with, and which status map its action looks them up in, are what
///     the code does, not metadata, so neither the running API nor the generator can work them out
///     by reflection. The script reads them from the source, as OpenApiResponseContractTests reads the
///     success statuses, and scripts/openapi.py runs it before generating the document; its
///     <c>--check</c> fails when the committed file is out of date. Each entry is a status and what
///     answers it: message constants (<c>ScopeMessages.ScopeNotFound</c>), and the markers
///     <see cref="Validation" />, <see cref="PasswordHash" /> and <see cref="RetryAdvisor" />.
/// </remarks>
public static class ErrorResponseCatalog
{
    public const string ResourceName = "ArturRios.Heimdall.WebApi.Documentation.error-responses.json";

    /// <summary>A validator's own message, which no map holds: 400.</summary>
    public const string Validation = "IValidator";

    /// <summary>A password hash derived through <c>PasswordHashGate.Shared</c>: the saturated gate's 503.</summary>
    public const string PasswordHash = "PasswordHashGate";

    /// <summary>A 429 whose handler can advise a <c>Retry-After</c>.</summary>
    public const string RetryAdvisor = "IRetryAfterAdvisor";

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<int, string[]>>> s_entries =
        new(Load);

    /// <summary>Every operation the catalog lists, by operation id.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<int, string[]>> Entries => s_entries.Value;

    /// <summary>The error statuses an operation's handler can answer, and what answers each.</summary>
    public static IReadOnlyDictionary<int, string[]> For(string operationId) =>
        Entries.TryGetValue(operationId, out var statuses) ? statuses : ReadOnlyDictionary<int, string[]>.Empty;

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<int, string[]>> Load()
    {
        using var stream = typeof(ErrorResponseCatalog).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException(
                               $"{ResourceName} is not embedded in the Web API assembly. It is listed as an "
                               + "EmbeddedResource in ArturRios.Heimdall.WebApi.csproj and written by "
                               + "scripts/error_responses.py.");

        var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string[]>>>(stream)
                  ?? throw new InvalidOperationException($"{ResourceName} is empty.");

        return raw.ToDictionary(
            operation => operation.Key,
            IReadOnlyDictionary<int, string[]> (operation) => operation.Value.ToDictionary(
                status => int.Parse(status.Key, CultureInfo.InvariantCulture),
                status => status.Value));
    }
}

/// <summary>
///     Documents every error response an operation can answer, with the body and media types the
///     server really writes for it.
/// </summary>
/// <remarks>
///     <para>
///         The sources are <see cref="ErrorSource" />'s. Those the pipeline adds are read from the
///         action's metadata: a 401 from authentication on every operation that is not
///         <c>[AllowAnonymous]</c>, a 403 from <c>[RoleRequirement]</c>, a 403 from the
///         subject-rights guard on every authenticated operation that is not <c>[DataSubjectRight]</c>,
///         the rate limiter's 429 under <c>[EnableRateLimiting]</c>, a 400 from
///         <c>MalformedRequestFilter</c> wherever a body or query is bound, the exception
///         middleware's 500 everywhere, Kestrel's refusals of a request body (400, 408, 413) wherever a
///         body is bound, and Kestrel's own refusals of a request line or headers (400, 408, 414, 431,
///         505, without a body) everywhere. The handler's are <see cref="ErrorResponseCatalog" />'s.
///     </para>
///     <para>
///         One schema per response, the narrowest that every body the sources write validates
///         against. <c>ProcessOutput</c> when only a filter or middleware answers without
///         <c>data</c>. The action's own envelope when the handler answers too: it is what the handler
///         writes, with a null <c>data</c>, and a <c>ProcessOutput</c> or a <c>DataOutput&lt;object?&gt;</c>
///         with a null <c>data</c> validates against it as well, because <c>data</c> is nullable and
///         no member is required. <c>ObjectDataOutput</c> (<c>data</c> any value) for an action without
///         an envelope of its own, or wherever the exception middleware's empty-string <c>data</c> meets
///         another body, and <c>StringDataOutput</c> where only the exception middleware writes. No
///         content where only Kestrel answers, which it does with an empty body.
///     </para>
///     <para>
///         A response ApiExplorer already lists (a <c>[ProducesResponseType]</c> 409 or 503) is kept as
///         it is. Every response is then put in status order.
///     </para>
/// </remarks>
public sealed class ErrorResponseFilter : IOperationFilter
{
    private const string TextJson = "text/json";

    // Spelled out rather than read from ReasonPhrases, whose wording for a status can change between
    // releases (422 became "Unprocessable Content" in RFC 9110), so the document's text stays put.
    private static readonly Dictionary<int, string> s_reasons = new()
    {
        [StatusCodes.Status400BadRequest] = "Bad Request",
        [StatusCodes.Status401Unauthorized] = "Unauthorized",
        [StatusCodes.Status403Forbidden] = "Forbidden",
        [StatusCodes.Status404NotFound] = "Not Found",
        [StatusCodes.Status408RequestTimeout] = "Request Timeout",
        [StatusCodes.Status409Conflict] = "Conflict",
        [StatusCodes.Status413PayloadTooLarge] = "Payload Too Large",
        [StatusCodes.Status414UriTooLong] = "URI Too Long",
        [StatusCodes.Status422UnprocessableEntity] = "Unprocessable Entity",
        [StatusCodes.Status429TooManyRequests] = "Too Many Requests",
        [StatusCodes.Status431RequestHeaderFieldsTooLarge] = "Request Header Fields Too Large",
        [StatusCodes.Status500InternalServerError] = "Internal Server Error",
        [StatusCodes.Status503ServiceUnavailable] = "Service Unavailable",
        [StatusCodes.Status505HttpVersionNotsupported] = "HTTP Version Not Supported"
    };

    // Kestrel's own rejections while it parses the request line and headers, before the API sees the
    // request (KestrelBadHttpRequestException): a malformed request line or header, a missing or invalid
    // Host, an invalid Content-Length or transfer coding (400); headers sent too slowly (408); a request
    // line over MaxRequestLineSize (414); headers over MaxRequestHeadersTotalSize or MaxRequestHeaderCount
    // (431); an HTTP version it does not speak (505). It answers them itself, with Content-Length: 0. Any
    // request can meet them, so every operation lists them. 405 is left out: Kestrel answers it only for
    // an asterisk-form or authority-form target, which no operation's path is.
    private static readonly int[] s_serverStatuses =
    [
        StatusCodes.Status400BadRequest, StatusCodes.Status408RequestTimeout, StatusCodes.Status414UriTooLong,
        StatusCodes.Status431RequestHeaderFieldsTooLarge, StatusCodes.Status505HttpVersionNotsupported
    ];

    // The rejections Kestrel throws into the API while the request body is read, which only an action
    // that binds a body does: a body cut short of its Content-Length or badly chunked (400), one sent
    // below MinRequestBodyDataRate (408), one over MaxRequestBodySize (413). BodyModelBinder passes a
    // BadHttpRequestException through, so ExceptionMiddleware answers it.
    private static readonly int[] s_requestBodyStatuses =
    [
        StatusCodes.Status400BadRequest, StatusCodes.Status408RequestTimeout, StatusCodes.Status413PayloadTooLarge
    ];

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo is not { } method
            || context.ApiDescription.ActionDescriptor is not ControllerActionDescriptor action)
        {
            return;
        }

        var catalog = ErrorResponseCatalog.For($"{action.ControllerName}_{action.ActionName}");
        var sources = Sources(method, context, catalog);
        OpenApiResponses existing = operation.Responses ?? [];
        var envelope = Envelope(existing);
        var responses = existing.ToDictionary(response => response.Key, response => response.Value);

        foreach (var (status, kinds) in sources)
        {
            var key = status.ToString(CultureInfo.InvariantCulture);

            if (responses.ContainsKey(key))
            {
                continue;
            }

            responses[key] = Response(status, kinds, envelope, context,
                catalog.TryGetValue(status, out var entries) && entries.Contains(ErrorResponseCatalog.RetryAdvisor));
        }

        OpenApiResponses ordered = [];

        foreach (var (key, response) in responses.OrderBy(response => int.Parse(response.Key, CultureInfo.InvariantCulture)))
        {
            ordered[key] = response;
        }

        operation.Responses = ordered;
    }

    private static SortedDictionary<int, SortedSet<ErrorSource>> Sources(
        MethodInfo method, OperationFilterContext context, IReadOnlyDictionary<int, string[]> catalog)
    {
        var sources = new SortedDictionary<int, SortedSet<ErrorSource>>();

        void Add(int status, ErrorSource source)
        {
            if (!sources.TryGetValue(status, out var set))
            {
                sources[status] = set = [];
            }

            set.Add(source);
        }

        var attributes = method.GetCustomAttributes(inherit: true)
            .Concat(method.DeclaringType?.GetCustomAttributes(inherit: true) ?? [])
            .ToList();

        // A body or a query value: what MVC binds from the request beyond the route, whose values the
        // route's own constraints (every id is {id:guid}) have already checked.
        if (context.ApiDescription.ParameterDescriptions.Any(parameter =>
                parameter.Source is { IsFromRequest: true } source && source != BindingSource.Path))
        {
            Add(StatusCodes.Status400BadRequest, ErrorSource.Malformed);
        }

        foreach (var (status, entries) in catalog)
        {
            if (entries.Any(entry => entry is not ErrorResponseCatalog.PasswordHash and not ErrorResponseCatalog.RetryAdvisor))
            {
                Add(status, ErrorSource.Handler);
            }

            if (entries.Contains(ErrorResponseCatalog.PasswordHash))
            {
                Add(status, ErrorSource.PasswordHash);
            }
        }

        if (!attributes.OfType<AllowAnonymousAttribute>().Any())
        {
            Add(StatusCodes.Status401Unauthorized, ErrorSource.Authentication);

            if (attributes.OfType<RoleRequirementAttribute>().Any())
            {
                Add(StatusCodes.Status403Forbidden, ErrorSource.Role);
            }

            if (!attributes.OfType<DataSubjectRightAttribute>().Any())
            {
                Add(StatusCodes.Status403Forbidden, ErrorSource.SubjectRights);
            }
        }

        if (method.GetCustomAttribute<DisableRateLimitingAttribute>() is null
            && (method.GetCustomAttribute<EnableRateLimitingAttribute>()
                ?? method.DeclaringType?.GetCustomAttribute<EnableRateLimitingAttribute>()) is not null)
        {
            Add(StatusCodes.Status429TooManyRequests, ErrorSource.RateLimit);
        }

        Add(StatusCodes.Status500InternalServerError, ErrorSource.Exception);

        if (context.ApiDescription.ParameterDescriptions.Any(parameter => parameter.Source == BindingSource.Body))
        {
            foreach (var status in s_requestBodyStatuses)
            {
                Add(status, ErrorSource.RequestBody);
            }
        }

        foreach (var status in s_serverStatuses)
        {
            Add(status, ErrorSource.Server);
        }

        return sources;
    }

    /// <summary>The schema of the operation's lowest 2xx response with a body, which is its envelope.</summary>
    private static IOpenApiSchema? Envelope(OpenApiResponses responses) =>
        responses
            .Where(response => response.Key.StartsWith('2') && response.Value.Content is { Count: > 0 })
            .OrderBy(response => response.Key, StringComparer.Ordinal)
            .Select(response => response.Value.Content!.Values.First().Schema)
            .FirstOrDefault();

    private static OpenApiResponse Response(
        int status, SortedSet<ErrorSource> sources, IOpenApiSchema? envelope, OperationFilterContext context,
        bool retryAdvised)
    {
        // Kestrel's own rejections carry no body, so they add no media type or schema of their own.
        SortedSet<ErrorSource> bodies = [.. sources.Where(source => source is not ErrorSource.Server)];

        // ExceptionMiddleware and the rate limiter write with WriteAsJsonAsync, whatever the request
        // accepts; every other source is an ObjectResult, negotiated like the action's own answer.
        string[] mediaTypes =
            bodies.All(source => source is ErrorSource.RateLimit or ErrorSource.Exception or ErrorSource.RequestBody)
                ? [MediaTypeNames.Application.Json]
                : [MediaTypeNames.Application.Json, TextJson];

        var retryAfter = sources.Contains(ErrorSource.RateLimit)
                         || sources.Contains(ErrorSource.PasswordHash)
                         || (status == StatusCodes.Status429TooManyRequests && retryAdvised);

        return new OpenApiResponse
        {
            Description = $"{Reason(status)}. {string.Join(" ", sources.Select(source => Sentence(source, status)))}",
            Headers = retryAfter
                ? new Dictionary<string, IOpenApiHeader>
                {
                    [HeaderNames.RetryAfter] = new OpenApiHeader
                    {
                        Description = "Seconds to wait before trying again.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.Integer }
                    }
                }
                : null,
            Content = bodies.Count == 0
                ? null
                : mediaTypes.ToDictionary(
                    mediaType => mediaType,
                    IOpenApiMediaType (_) => new OpenApiMediaType { Schema = Schema(bodies, envelope, context) })
        };
    }

    private static IOpenApiSchema Schema(
        SortedSet<ErrorSource> sources, IOpenApiSchema? envelope, OperationFilterContext context)
    {
        // ExceptionMiddleware's bodies carry an empty string as data, which no envelope but
        // StringDataOutput and ObjectDataOutput admits.
        var text = sources.Any(source => source is ErrorSource.Exception or ErrorSource.RequestBody);

        var type = sources switch
        {
            _ when sources.Contains(ErrorSource.Handler) && text => typeof(DataOutput<object>),
            _ when sources.Contains(ErrorSource.Handler) => null,
            _ when sources.All(source => source is ErrorSource.Exception or ErrorSource.RequestBody) =>
                typeof(DataOutput<string>),
            _ when text || sources.Any(source => source is ErrorSource.Malformed or ErrorSource.RateLimit) =>
                typeof(DataOutput<object>),
            _ => typeof(ProcessOutput)
        };

        return type is null
            ? envelope ?? context.SchemaGenerator.GenerateSchema(typeof(DataOutput<object>), context.SchemaRepository)
            : context.SchemaGenerator.GenerateSchema(type, context.SchemaRepository);
    }

    private static string Reason(int status) =>
        s_reasons.TryGetValue(status, out var reason) ? reason : status.ToString(CultureInfo.InvariantCulture);

    private static string Sentence(ErrorSource source, int status) => source switch
    {
        ErrorSource.RequestBody => status switch
        {
            StatusCodes.Status408RequestTimeout =>
                "Kestrel refused the request body as the action read it: it arrived slower than the minimum data rate (240 bytes a second by default). The body's `data` is an empty string.",
            StatusCodes.Status413PayloadTooLarge =>
                "Kestrel refused the request body as the action read it: it is larger than the maximum request body size (30,000,000 bytes by default). The body's `data` is an empty string.",
            _ =>
                "Kestrel refused the request body as the action read it: it was cut short of its Content-Length, or badly chunked. The body's `data` is an empty string."
        },
        ErrorSource.Server => status switch
        {
            StatusCodes.Status408RequestTimeout =>
                "Kestrel refused the request before the API saw it: its headers arrived too slowly. No body.",
            StatusCodes.Status414UriTooLong =>
                "Kestrel refused the request before the API saw it: its request line is longer than the limit (8 KB by default). No body.",
            StatusCodes.Status431RequestHeaderFieldsTooLarge =>
                "Kestrel refused the request before the API saw it: its headers are larger than the limit (32 KB, or 100 headers, by default). No body.",
            StatusCodes.Status505HttpVersionNotsupported =>
                "Kestrel refused the request before the API saw it: it names an HTTP version Kestrel does not speak. No body.",
            _ =>
                "Kestrel refused the request before the API saw it: a malformed request line or header, a missing or invalid Host, or an invalid Content-Length or transfer coding. No body."
        },
        ErrorSource.Malformed => "The request could not be bound: its body or a query value is malformed or missing.",
        ErrorSource.Handler =>
            "The handler refused the request with a message the operation's status map answers with this status.",
        ErrorSource.Authentication =>
            "The token is missing, malformed, expired or revoked, is a 2FA challenge token, or names an identity that no longer exists.",
        ErrorSource.Role => "The token is valid but the caller does not hold a required role.",
        ErrorSource.SubjectRights => "The token admits only the data-subject-rights operations.",
        ErrorSource.RateLimit => "The caller's budget for this operation is spent until the window ends.",
        ErrorSource.Exception => "An unexpected failure: the body's `data` is an empty string.",
        ErrorSource.PasswordHash =>
            "Password hashing is saturated: the request was not at fault, and may be retried after `Retry-After` seconds.",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
    };
}
