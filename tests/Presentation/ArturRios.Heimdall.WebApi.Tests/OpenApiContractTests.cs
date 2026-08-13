using System.Text.Json;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

// Guards the published contract against the defect fixed on 2026-08-12: the mediator's input DTOs
// double as the wire DTOs, so a property the server assigns from the route or the token is
// indistinguishable, to the framework, from one the client sends. Both leaks it produced are
// checked here, over the document itself rather than over the DTOs — a DTO-level reflection test
// would also flag the commands the controller constructs in code, which never reach the contract at
// all. These are [UnitFact]: no database, no host, just the committed file.
public class OpenApiContractTests
{
    // Server-populated on every endpoint without exception, unlike ScopeId — LoginCommand,
    // PasswordRecoveryCommand and GoogleSignInCommand take a genuine client-supplied ScopeId on
    // routes with no {scopeId} segment, which is why the route-collision test below keys off the
    // operation's own path parameters instead of a name blocklist.
    private static readonly string[] ServerPopulated = ["actingPersonId", "actingRole"];

    private static readonly string[] HttpMethods =
        ["get", "put", "post", "delete", "patch", "options", "head"];

    [UnitFact]
    public void GivenPublishedDocument_WhenOperationsInspected_ThenNothingRepeatsAPathParameter()
    {
        using var document = LoadDocument();
        var violations = new List<string>();

        foreach (var (route, method, operation) in Operations(document))
        {
            var pathNames = ParameterNames(operation, "path");

            violations.AddRange(ParameterNames(operation, "query")
                .Where(name => pathNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Select(name => $"{method} {route}: query parameter '{name}' repeats the route"));

            violations.AddRange(RequestBodyPropertyNames(document, operation)
                .Where(name => pathNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Select(name => $"{method} {route}: body property '{name}' repeats the route"));
        }

        Assert.True(violations.Count == 0, Report(violations));
    }

    [UnitFact]
    public void GivenPublishedDocument_WhenOperationsInspected_ThenNothingExposesTheActingCaller()
    {
        using var document = LoadDocument();
        var violations = new List<string>();

        foreach (var (route, method, operation) in Operations(document))
        {
            violations.AddRange(ParameterNames(operation, "query")
                .Where(IsServerPopulated)
                .Select(name => $"{method} {route}: query parameter '{name}'"));

            violations.AddRange(RequestBodyPropertyNames(document, operation)
                .Where(IsServerPopulated)
                .Select(name => $"{method} {route}: body property '{name}'"));
        }

        Assert.True(violations.Count == 0, Report(violations));
    }

    private static bool IsServerPopulated(string name) =>
        ServerPopulated.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string Report(List<string> violations) =>
        $"The published contract exposes {violations.Count} server-populated field(s):"
        + Environment.NewLine
        + string.Join(Environment.NewLine, violations);

    private static JsonDocument LoadDocument() =>
        JsonDocument.Parse(File.ReadAllText(DocumentPath()));

    // Overridable so the test can be pointed at a document out of git history, which is how it was
    // shown to fail before the fix landed.
    private static string DocumentPath() =>
        Environment.GetEnvironmentVariable("HEIMDALL_OPENAPI_DOCUMENT")
        ?? Path.Combine(RepositoryRoot(), "docs", "openapi", "heimdall.json");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "src", "ArturRios.Heimdall.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException(
                   $"Could not locate the repository root from {AppContext.BaseDirectory}");
    }

    private static IEnumerable<(string Route, string Method, JsonElement Operation)> Operations(
        JsonDocument document)
    {
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject()
                         .Where(candidate => HttpMethods.Contains(candidate.Name)))
            {
                yield return (path.Name, operation.Name.ToUpperInvariant(), operation.Value);
            }
        }
    }

    private static List<string> ParameterNames(JsonElement operation, string location) =>
        operation.TryGetProperty("parameters", out var parameters)
            ? parameters.EnumerateArray()
                .Where(parameter => parameter.GetProperty("in").GetString() == location)
                .Select(parameter => parameter.GetProperty("name").GetString()!)
                .ToList()
            : [];

    private static IEnumerable<string> RequestBodyPropertyNames(
        JsonDocument document, JsonElement operation)
    {
        if (!operation.TryGetProperty("requestBody", out var body)
            || !body.TryGetProperty("content", out var content))
        {
            return [];
        }

        return content.EnumerateObject()
            .Select(media => media.Value.GetProperty("schema"))
            .SelectMany(schema => PropertyNames(document, schema))
            .Distinct();
    }

    private static IEnumerable<string> PropertyNames(JsonDocument document, JsonElement schema)
    {
        var resolved = schema.TryGetProperty("$ref", out var reference)
            ? Resolve(document, reference.GetString()!)
            : schema;

        return resolved.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Select(property => property.Name).ToList()
            : [];
    }

    private static JsonElement Resolve(JsonDocument document, string reference) =>
        document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(reference[(reference.LastIndexOf('/') + 1)..]);
}
