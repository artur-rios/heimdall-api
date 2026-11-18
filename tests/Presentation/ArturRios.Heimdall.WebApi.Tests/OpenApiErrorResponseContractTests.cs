using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.WebApi.Documentation;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     Holds the document's error responses to their sources: the handlers' messages looked up in the
///     status maps their actions pass (through <see cref="ErrorResponseCatalog" />, which
///     scripts/error_responses.py derives from the source), the password-hash gate, and the pipeline's
///     filters and middleware, each with the body it writes.
/// </summary>
/// <remarks>
///     scripts/openapi.py --check already fails when the catalog is not what the script derives. These
///     check the derivation itself against the running code: that each message the catalog lists is
///     one its action's map really gives that status (by the message's text, as the server looks it
///     up), that the source names it, that every error a handler fails with directly is listed, and
///     that the hashing paths are the ones the source reaches from both ends.
/// </remarks>
public partial class OpenApiResponseContractTests
{
    // What the derivation follows, as scripts/error_responses.py's EXCLUDED_FOLDERS leaves it.
    private static readonly string[] s_excludedFolders =
        ["ArturRios.Heimdall.Domain", "Messages", "Controllers", "Input", "Output", "Documentation", "Migrations", "EntityMaps"];

    private static readonly string[] s_markers =
        [ErrorResponseCatalog.Validation, ErrorResponseCatalog.PasswordHash, ErrorResponseCatalog.RetryAdvisor];

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"\b(?:class|interface|record|struct)\s+(\w+)")]
    private static partial Regex Declaration();

    [GeneratedRegex(@"\b(?:class|record)\s+\w+(?:<[^>]*>)?\s*(?:\((?:[^()]|\([^()]*\))*\))?\s*:\s*([^{;]+)")]
    private static partial Regex TypeHeader();

    [GeneratedRegex(@"\b([A-Z]\w*)\b")]
    private static partial Regex Identifier();

    [UnitFact]
    public void GivenCatalog_WhenListed_ThenEachOperationIsPublished()
    {
        using var document = LoadDocument();

        var published = Operations(document).Select(operation => operation.OperationId).ToHashSet();

        Assert.NotEmpty(ErrorResponseCatalog.Entries);
        Assert.All(ErrorResponseCatalog.Entries.Keys, operationId => Assert.Contains(operationId, published));
    }

    [UnitFact]
    public void GivenCatalog_WhenItsMessagesAreLookedUpInTheirActionsStatusMaps_ThenEachGivesItsStatus()
    {
        var violations = new List<string>();
        var messages = 0;

        foreach (var (operationId, statuses) in ErrorResponseCatalog.Entries)
        {
            var map = ActionStatusMap(operationId);

            foreach (var (status, entries) in statuses)
            {
                foreach (var symbol in entries.Where(entry => !s_markers.Contains(entry)))
                {
                    messages++;

                    if (!map.TryGetValue(SymbolValue(symbol), out var mapped) || mapped != status)
                    {
                        violations.Add($"{operationId}: {symbol} is listed under {status}, the map gives "
                                       + (map.TryGetValue(SymbolValue(symbol), out var actual)
                                           ? actual.ToString(CultureInfo.InvariantCulture)
                                           : "nothing"));
                    }
                }
            }
        }

        Assert.NotEqual(0, messages);
        Assert.True(
            violations.Count == 0,
            $"{violations.Count} catalog entr(ies) disagree with the status maps:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [UnitFact]
    public void GivenCatalog_WhenItsMessagesAreSearchedForInTheSource_ThenTheCodeNamesEach()
    {
        // Validators are under Input/, which the derivation reaches only through IValidator<T>, so the
        // search is over all the code but the messages' own declarations.
        var code = string.Join('\n', Directory.EnumerateFiles(Repository.PathTo("src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj" or "Messages"))
            .Select(path => LineComment().Replace(BlockComment().Replace(File.ReadAllText(path), string.Empty), string.Empty)));

        var unnamed = ErrorResponseCatalog.Entries.Values
            .SelectMany(statuses => statuses.Values.SelectMany(entries => entries))
            .Where(entry => !s_markers.Contains(entry) && !entry.StartsWith("RelationalErrors.", StringComparison.Ordinal))
            .Distinct()
            .Where(symbol => !Regex.IsMatch(code, $@"\b{Regex.Escape(symbol)}\b"))
            .ToList();

        Assert.Empty(unnamed);
    }

    [UnitFact]
    public void GivenHandlersDirectErrors_WhenLookedUpInTheirStatusMaps_ThenTheCatalogListsEach()
    {
        const string call = ".WithError(";
        var violations = new List<string>();
        var errors = 0;

        foreach (var (operationId, _) in Actions())
        {
            var source = ActionSourceOf(operationId);
            var input = MediatorInput().Match(source);

            if (!input.Success)
            {
                continue;
            }

            var map = ActionStatusMap(operationId);
            var handler = HandlerSource(input.Groups[1].Value);
            var listed = ErrorResponseCatalog.For(operationId);

            for (var index = handler.IndexOf(call, StringComparison.Ordinal);
                 index >= 0;
                 index = handler.IndexOf(call, index + call.Length, StringComparison.Ordinal))
            {
                foreach (Match reference in MessageReference().Matches(Argument(handler, index + call.Length)))
                {
                    var symbol = $"{reference.Groups[1].Value}.{reference.Groups[2].Value}";

                    if (!map.TryGetValue(SymbolValue(symbol), out var status) || status < 400)
                    {
                        continue;
                    }

                    errors++;

                    if (!listed.TryGetValue(status, out var entries) || !entries.Contains(symbol))
                    {
                        violations.Add($"{operationId}: fails with {symbol} ({status}), which the catalog does not list");
                    }
                }
            }
        }

        Assert.NotEqual(0, errors);
        Assert.True(
            violations.Count == 0,
            $"{violations.Count} direct handler error(s) are missing from the catalog:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [UnitFact]
    public void GivenPasswordHashingPaths_WhenFoundFromTheSource_ThenExactlyThoseOperationsDocumentTheGates503()
    {
        using var document = LoadDocument();

        // From the other end than the script: start at the code that derives a hash and mark every
        // file that names a marked type, or an interface a marked type implements, until nothing
        // changes. A handler is on a hashing path when it is marked.
        var sources = FollowedSources();
        var declared = sources.SelectMany(source => source.Declared).ToHashSet();
        var marked = new HashSet<string>();
        var names = new HashSet<string>();

        void Absorb(SourceFile source)
        {
            marked.Add(source.Path);
            names.UnionWith(source.Declared);
            names.UnionWith(source.Bases.Where(name => name.StartsWith('I') && declared.Contains(name)));
        }

        foreach (var source in sources.Where(source => source.Code.Contains("PasswordHashGate.Shared.", StringComparison.Ordinal)))
        {
            Absorb(source);
        }

        Assert.NotEmpty(marked);

        bool changed;

        do
        {
            changed = false;

            foreach (var source in sources.Where(source => !marked.Contains(source.Path)))
            {
                if (source.Identifiers.Overlaps(names))
                {
                    Absorb(source);
                    changed = true;
                }
            }
        } while (changed);

        var hashing = Actions()
            .Select(action => action.OperationId)
            .Where(operationId => MediatorInput().Match(ActionSourceOf(operationId)) is { Success: true } input
                                  && marked.Contains(HandlerPath(input.Groups[1].Value)))
            .Order(StringComparer.Ordinal)
            .ToList();

        // The gate's 503 is the only one that carries Retry-After.
        var documenting = Operations(document)
            .Where(operation => operation.Operation.GetProperty("responses").TryGetProperty("503", out var response)
                                && response.TryGetProperty("headers", out var headers)
                                && headers.TryGetProperty("Retry-After", out _))
            .Select(operation => operation.OperationId)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Contains("Auth_Login", hashing);
        Assert.Equal(hashing, documenting);

        foreach (var operationId in hashing)
        {
            Assert.Contains(ErrorResponseCatalog.PasswordHash, ErrorResponseCatalog.For(operationId)[503]);
        }
    }

    [UnitFact]
    public void GivenCatalog_WhenDocumentRead_ThenEachStatusIsDocumentedWithTheActionsEnvelope()
    {
        using var document = LoadDocument();
        var operations = Operations(document).ToDictionary(operation => operation.OperationId);
        var violations = new List<string>();

        foreach (var (operationId, statuses) in ErrorResponseCatalog.Entries)
        {
            var operation = operations[operationId].Operation;
            var responses = operation.GetProperty("responses");
            var readsBody = operation.TryGetProperty("requestBody", out _);

            foreach (var status in statuses.Keys.Where(status => statuses[status].Any(entry => entry is not ErrorResponseCatalog.PasswordHash and not ErrorResponseCatalog.RetryAdvisor)))
            {
                // Where Kestrel can also refuse the body with the same status, ExceptionMiddleware's
                // empty-string data joins the handler's envelope, and only ObjectDataOutput holds both.
                var envelope = readsBody && s_requestBodyStatuses.Contains(status)
                    ? "#/components/schemas/ObjectDataOutput"
                    : EnvelopeOf(responses);

                if (!responses.TryGetProperty($"{status}", out var response))
                {
                    violations.Add($"{operationId}: {status} is not documented");
                }
                else if (SchemaReference(response) != envelope)
                {
                    violations.Add($"{operationId}: {status} documents {SchemaReference(response)}, the handler writes {envelope}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"{violations.Count} handler error status(es) are not documented as the handler writes them:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [UnitFact]
    public void GivenPublishedDocument_WhenRead_ThenEveryOperationDocuments500WithTheExceptionEnvelope()
    {
        using var document = LoadDocument();

        Assert.All(Operations(document), operation =>
        {
            var response = operation.Operation.GetProperty("responses").GetProperty("500");

            // ExceptionMiddleware writes DataOutput<string> with an empty data, as JSON whatever is accepted.
            Assert.Equal(["application/json"], MediaTypes(response));
            Assert.Equal("#/components/schemas/StringDataOutput", SchemaReference(response));
        });
    }

    [UnitFact]
    public void GivenAuthenticatedOperations_WhenTheir401And403Read_ThenEachHasTheBodyItsSourcesWrite()
    {
        using var document = LoadDocument();
        var subjectRights = Actions()
            .Where(action => action.Action.GetCustomAttribute<DataSubjectRightAttribute>() is not null)
            .Select(action => action.OperationId)
            .ToHashSet();
        var checkedOperations = 0;

        foreach (var (_, _, operationId, operation) in Operations(document))
        {
            var anonymous = operation.GetProperty("security").GetArrayLength() == 0;
            var role = operation.GetProperty("description").GetString()!.Contains("**Requires role:**", StringComparison.Ordinal);
            var responses = operation.GetProperty("responses");
            var listed = ErrorResponseCatalog.For(operationId);

            if (anonymous)
            {
                continue;
            }

            checkedOperations++;

            // AuthenticationMiddleware and the authorization filters write ProcessOutput: no data. When
            // the handler can answer the same status, its envelope (data null) holds both bodies.
            var expected401 = listed.ContainsKey(401) ? EnvelopeOf(responses) : "#/components/schemas/ProcessOutput";
            Assert.Equal(expected401, SchemaReference(responses.GetProperty("401")));

            if (role || !subjectRights.Contains(operationId))
            {
                var expected403 = listed.ContainsKey(403) ? EnvelopeOf(responses) : "#/components/schemas/ProcessOutput";
                Assert.Equal(expected403, SchemaReference(responses.GetProperty("403")));
            }
        }

        Assert.NotEqual(0, checkedOperations);

        var processOutput = Schemas(document).Single(schema => schema.Name == "ProcessOutput").Value;

        Assert.Equal(
            ["errors", "messages", "success", "timestamp"],
            Properties(processOutput).Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [UnitFact]
    public void GivenOperationsBindingABodyOrQuery_WhenRead_ThenEachDocuments400()
    {
        using var document = LoadDocument();

        var binding = Operations(document)
            .Where(operation => operation.Operation.TryGetProperty("requestBody", out _)
                                || (operation.Operation.TryGetProperty("parameters", out var parameters)
                                    && parameters.EnumerateArray().Any(parameter => parameter.GetProperty("in").GetString() == "query")))
            .ToList();

        Assert.NotEmpty(binding);
        Assert.All(binding, operation =>
            Assert.True(operation.Operation.GetProperty("responses").TryGetProperty("400", out _), operation.OperationId));
    }

    [UnitFact]
    public void GivenEmailBudgetOperations_WhenRead_ThenEachDocumentsItsHandlers429()
    {
        using var document = LoadDocument();
        var operations = Operations(document).ToDictionary(operation => operation.OperationId);

        string[] budgeted = ["Person_SendVerificationEmail", "Person_SendPasswordReset"];

        foreach (var operationId in budgeted)
        {
            var responses = operations[operationId].Operation.GetProperty("responses");

            Assert.Contains("PersonMessages.AccountEmailSentRecently", ErrorResponseCatalog.For(operationId)[429]);
            Assert.Equal(EnvelopeOf(responses), SchemaReference(responses.GetProperty("429")));
        }
    }

    /// <summary>The schema the action's handler writes an error in: its lowest 2xx envelope, or ObjectDataOutput.</summary>
    private static string EnvelopeOf(JsonElement responses) =>
        responses.EnumerateObject()
            .Where(response => response.Name.StartsWith('2') && response.Value.TryGetProperty("content", out _))
            .OrderBy(response => response.Name, StringComparer.Ordinal)
            .Select(response => SchemaReference(response.Value))
            .FirstOrDefault()
        ?? "#/components/schemas/ObjectDataOutput";

    private static string ActionSourceOf(string operationId) =>
        ActionSource(operationId[..operationId.IndexOf('_')], operationId[(operationId.IndexOf('_') + 1)..]);

    /// <summary>The status map the action passes, keyed by message text as the server looks it up.</summary>
    private static IReadOnlyDictionary<string, int> ActionStatusMap(string operationId)
    {
        var map = StatusMapArgument().Match(ActionSourceOf(operationId));

        Assert.True(map.Success, $"{operationId}: no statusMap argument");

        return StatusMap(map.Groups[1].Value);
    }

    /// <summary>The text of a message constant: one of the Messages classes, or the data library's RelationalErrors.</summary>
    private static string SymbolValue(string symbol)
    {
        var separator = symbol.IndexOf('.');
        var type = symbol[..separator] == nameof(RelationalErrors)
            ? typeof(RelationalErrors)
            : MessagesType(symbol[..separator]);
        var field = type.GetField(symbol[(separator + 1)..], BindingFlags.Public | BindingFlags.Static);

        Assert.True(field is not null, $"{symbol} is not a message constant");

        return (string)field.GetValue(null)!;
    }

    private static string HandlerPath(string input) =>
        Directory
            .EnumerateFiles(Repository.PathTo("src", "Application"), $"{input}Handler.cs", SearchOption.AllDirectories)
            .Single(path => Path.GetFileName(Path.GetDirectoryName(path)) == "Handlers");

    private sealed record SourceFile(
        string Path, string Code, HashSet<string> Declared, HashSet<string> Bases, HashSet<string> Identifiers);

    /// <summary>The code under src/ that the derivation follows, comments stripped.</summary>
    private static List<SourceFile> FollowedSources() =>
    [
        .. Directory.EnumerateFiles(Repository.PathTo("src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj" || s_excludedFolders.Contains(segment)))
            .Select(path =>
            {
                var code = LineComment().Replace(BlockComment().Replace(File.ReadAllText(path), string.Empty), string.Empty);

                return new SourceFile(
                    path,
                    code,
                    [.. Declaration().Matches(code).Select(match => match.Groups[1].Value)],
                    [.. TypeHeader().Matches(code).SelectMany(match => Identifier().Matches(match.Groups[1].Value)).Select(match => match.Groups[1].Value)],
                    [.. Identifier().Matches(code).Select(match => match.Groups[1].Value)]);
            })
    ];
}
