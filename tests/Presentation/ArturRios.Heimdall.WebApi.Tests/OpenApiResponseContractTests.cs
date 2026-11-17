using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Controllers;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     Holds the published document to what the API answers: the success statuses each operation
///     can return, which request bodies it cannot do without, and which response properties can be
///     null.
/// </summary>
/// <remarks>
///     <para>
///         The web UIs generate their clients from docs/openapi/heimdall.json, and a generated client
///         trusts it strictly: a 201 the document does not declare fails a create that worked, an
///         optional body turns the method into one that accepts only the lowest declared status, and
///         a null in a property the document calls non-nullable fails the parse. Each of the three was
///         wrong in the document until 2026-11-17.
///     </para>
///     <para>
///         The success status is worked out the way the server works it out. An action passes its
///         handler's output to <c>ToActionResult(statusMap:)</c>, which answers the status its map gives
///         the output's first message, and 200 for a message the map does not hold. So the statuses an
///         operation can answer are its handler's success messages (every <c>WithMessage</c> in the
///         handler), looked up in the map the action passes. Both are read from the source, as
///         <c>PasswordHashGateCoverageTests</c> reads it: the status map is an argument in the action's
///         body, not metadata reflection can see. A handler or action that stops matching the
///         patterns below fails the test rather than being skipped.
///     </para>
///     <para>
///         [UnitFact]: no database, no host, just the committed document and the source.
///     </para>
/// </remarks>
public partial class OpenApiResponseContractTests
{
    private static readonly string[] s_httpMethods =
        ["get", "put", "post", "delete", "patch", "options", "head"];

    // The operations whose success status is not their handler's message looked up in a status map,
    // with the statuses they answer on success. Each name must be an operation, so the list cannot
    // go stale.
    private static readonly Dictionary<string, int[]> s_outsideStatusMaps = new()
    {
        // ToActionResult(HttpStatusCodes.Ok): a fixed status and no handler.
        ["HealthCheck_HelloWorld"] = [200],
        // 200 when every dependency is ready; the 503 otherwise is not a success.
        ["HealthCheck_Ready"] = [200],
        // Content(...): the key set itself, not an envelope.
        ["SigningKeys_KeySet"] = [200],
        // NoContent() on success; the status map serves only the refusals.
        ["ScopeInvitation_Withdraw"] = [204]
    };

    // The properties that refer to another schema and are never null: each is a `required`
    // non-nullable member, so the document keeps the plain reference. Anything else that refers to
    // another schema must be marked nullable. Compared for equality, so the list cannot go stale.
    private static readonly string[] s_neverNullReferences =
    [
        "DataExportCommandOutput.processing",
        "DataExportCommandOutput.security",
        "DataExportCommandOutput.subject"
    ];

    // Response properties known to be null at run time, beyond the envelopes' `data`.
    private static readonly string[] s_knownNullable = ["DataExportCommandOutput.erasure"];

    [GeneratedRegex(@"Execute\w*Async<\s*(\w+)\s*,")]
    private static partial Regex MediatorInput();

    [GeneratedRegex(@"statusMap:\s*(\w+)\.StatusCodes")]
    private static partial Regex StatusMapArgument();

    [GeneratedRegex(@"\b(\w+Messages)\.(\w+)\b")]
    private static partial Regex MessageReference();

    [GeneratedRegex(@"(\w+)\(")]
    private static partial Regex MethodName();

    [UnitFact]
    public void GivenPublishedDocument_WhenSuccessStatusesRead_ThenEachOperationDeclaresWhatItAnswers()
    {
        using var document = LoadDocument();
        var violations = new List<string>();
        var operations = 0;

        foreach (var (route, method, operationId, operation) in Operations(document))
        {
            operations++;

            var declared = SuccessStatuses(operation);
            HashSet<int> answered = s_outsideStatusMaps.TryGetValue(operationId, out var statuses)
                ? [.. statuses]
                : AnsweredSuccessStatuses(operationId);

            if (!declared.SetEquals(answered))
            {
                violations.Add(
                    $"{method} {route} ({operationId}): declares [{string.Join(", ", declared.Order())}], "
                    + $"answers [{string.Join(", ", answered.Order())}]");
            }
        }

        Assert.NotEqual(0, operations);
        Assert.True(
            violations.Count == 0,
            $"{violations.Count} operation(s) declare other success statuses than they answer:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [UnitFact]
    public void GivenOperationsOutsideStatusMaps_WhenListed_ThenEachIsAPublishedOperation()
    {
        using var document = LoadDocument();

        var published = Operations(document).Select(operation => operation.OperationId).ToHashSet();

        Assert.All(s_outsideStatusMaps.Keys, operationId => Assert.Contains(operationId, published));
    }

    [UnitFact]
    public void GivenPublishedDocument_WhenRequestBodiesRead_ThenEveryBodyTheActionNeedsIsRequired()
    {
        using var document = LoadDocument();
        var operations = Operations(document).ToDictionary(operation => operation.OperationId);
        var violations = new List<string>();
        var bodies = 0;

        foreach (var (operationId, action) in Actions())
        {
            Assert.True(operations.TryGetValue(operationId, out var published), $"{operationId} is not published");

            var body = action.GetParameters().SingleOrDefault(parameter =>
                parameter.GetCustomAttribute<FromBodyAttribute>() is not null);
            var hasBody = published.Operation.TryGetProperty("requestBody", out var requestBody);

            if (body is null)
            {
                if (hasBody)
                {
                    violations.Add($"{operationId}: documents a request body the action does not bind");
                }

                continue;
            }

            bodies++;

            var required = hasBody
                           && requestBody.TryGetProperty("required", out var flag)
                           && flag.GetBoolean();

            if (!hasBody || required == AcceptsEmptyBody(body))
            {
                violations.Add(
                    $"{operationId}: the body is {(AcceptsEmptyBody(body) ? "optional" : "required")}, "
                    + $"the document says {(hasBody ? required ? "required" : "optional" : "nothing")}");
            }
        }

        Assert.NotEqual(0, bodies);
        Assert.True(
            violations.Count == 0,
            $"{violations.Count} request body declaration(s) do not match the action:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [UnitFact]
    public void GivenPublishedDocument_WhenEnvelopesRead_ThenTheirDataIsNullable()
    {
        using var document = LoadDocument();

        var envelopes = Schemas(document)
            .Where(schema => Properties(schema.Value).Any(property => property.Name == "data"))
            .ToList();
        var notNullable = envelopes
            .Where(schema => !IsNullable(Properties(schema.Value).Single(property => property.Name == "data").Value))
            .Select(schema => schema.Name)
            .ToList();

        Assert.NotEmpty(envelopes);
        Assert.True(
            notNullable.Count == 0,
            "DataOutput<T>.Data is T? and is written as null whenever there is no payload, but these "
            + $"envelopes document it as never null: {string.Join(", ", notNullable)}");
    }

    [UnitFact]
    public void GivenPublishedDocument_WhenKnownNullablePropertiesRead_ThenEachIsNullable()
    {
        using var document = LoadDocument();

        var notNullable = s_knownNullable
            .Where(name => !IsNullable(Property(document, name)))
            .ToList();

        Assert.Empty(notNullable);
    }

    [UnitFact]
    public void GivenPublishedDocument_WhenReferencePropertiesRead_ThenOnlyNeverNullMembersArePlainReferences()
    {
        using var document = LoadDocument();

        var plainReferences = Schemas(document)
            .SelectMany(schema => Properties(schema.Value)
                .Where(property => property.Value.TryGetProperty("$ref", out _))
                .Select(property => $"{schema.Name}.{property.Name}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(s_neverNullReferences, plainReferences);
    }

    [UnitFact]
    public void GivenNeverNullReferences_WhenTheirMembersRead_ThenEachIsRequiredAndNonNullable()
    {
        var nullability = new NullabilityInfoContext();

        foreach (var name in s_neverNullReferences)
        {
            var property = typeof(DataExportCommandOutput).GetProperty(
                name[(name.IndexOf('.') + 1)..],
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            Assert.NotNull(property);
            Assert.Equal(NullabilityState.NotNull, nullability.Create(property).ReadState);
            Assert.NotNull(property.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>());
        }
    }

    /// <summary>
    ///     The success statuses an operation answers: its handler's success messages, each looked up in
    ///     the status map its action passes, 200 for a message the map does not hold (and for a handler
    ///     that sets none).
    /// </summary>
    private static HashSet<int> AnsweredSuccessStatuses(string operationId)
    {
        var separator = operationId.IndexOf('_');
        var controller = operationId[..separator];
        var action = operationId[(separator + 1)..];

        var source = ActionSource(controller, action);
        var input = MediatorInput().Match(source);
        var map = StatusMapArgument().Match(source);

        Assert.True(input.Success, $"{operationId}: no mediator call found in {controller}Controller.{action}");
        Assert.True(map.Success, $"{operationId}: no statusMap argument found in {controller}Controller.{action}");

        var statusCodes = StatusMap(map.Groups[1].Value);
        var answered = SuccessMessages(HandlerSource(input.Groups[1].Value))
            .Select(message => statusCodes.TryGetValue(message, out var status) ? status : 200)
            .ToHashSet();

        return answered.Count == 0 ? [200] : answered;
    }

    /// <summary>The body of one action, read from its controller's source.</summary>
    /// <remarks>
    ///     A member starts at a line indented four spaces that begins with <c>public</c> and ends at the
    ///     next line that is four spaces and a closing brace, which is how dotnet format lays every
    ///     controller out. The action's name is the first identifier followed by a parenthesis.
    /// </remarks>
    private static string ActionSource(string controller, string action)
    {
        var path = Repository.PathTo(
            "src", "Presentation", "ArturRios.Heimdall.WebApi", "Controllers", $"{controller}Controller.cs");
        var lines = File.ReadAllText(path).ReplaceLineEndings("\n").Split('\n');
        var members = new List<string>();

        for (var start = 0; start < lines.Length; start++)
        {
            if (!lines[start].StartsWith("    public ", StringComparison.Ordinal))
            {
                continue;
            }

            var end = Array.FindIndex(lines, start, line => line.TrimEnd() == "    }");

            if (end < 0)
            {
                break;
            }

            members.Add(string.Join('\n', lines[start..(end + 1)]));
            start = end;
        }

        var matches = members
            .Where(member => MethodName().Match(member) is { Success: true } name && name.Groups[1].Value == action)
            .ToList();

        Assert.True(matches.Count == 1, $"Expected one {action} in {path}, found {matches.Count}");

        return matches[0];
    }

    /// <summary>The source of the handler that takes <paramref name="input" />.</summary>
    private static string HandlerSource(string input)
    {
        var handlers = Directory
            .EnumerateFiles(Repository.PathTo("src", "Application"), $"{input}Handler.cs", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(Path.GetDirectoryName(path)) == "Handlers")
            .ToList();

        Assert.True(handlers.Count == 1, $"Expected one {input}Handler.cs under src/Application, found {handlers.Count}");

        return File.ReadAllText(handlers[0]);
    }

    /// <summary>
    ///     The values of every message a handler passes to <c>WithMessage</c>: the messages of its
    ///     successful outputs. A conditional argument contributes each message it names.
    /// </summary>
    private static List<string> SuccessMessages(string handler)
    {
        const string call = ".WithMessage(";
        var messages = new List<string>();

        for (var index = handler.IndexOf(call, StringComparison.Ordinal);
             index >= 0;
             index = handler.IndexOf(call, index + call.Length, StringComparison.Ordinal))
        {
            var argument = Argument(handler, index + call.Length);
            var references = MessageReference().Matches(argument);

            Assert.True(references.Count > 0, $"WithMessage({argument}) names no message constant");

            messages.AddRange(references.Select(reference =>
                MessageValue(reference.Groups[1].Value, reference.Groups[2].Value)));
        }

        return messages;
    }

    /// <summary>The text between an opening parenthesis (just before <paramref name="start" />) and its match.</summary>
    private static string Argument(string source, int start)
    {
        var depth = 1;

        for (var index = start; index < source.Length; index++)
        {
            depth += source[index] switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0
            };

            if (depth == 0)
            {
                return source[start..index];
            }
        }

        throw new InvalidOperationException($"Unbalanced parentheses after position {start}");
    }

    private static string MessageValue(string type, string name)
    {
        var field = MessagesType(type).GetField(name, BindingFlags.Public | BindingFlags.Static);

        Assert.True(field is not null, $"{type}.{name} is not a message constant");

        return (string)field.GetValue(null)!;
    }

    private static IReadOnlyDictionary<string, int> StatusMap(string type)
    {
        var field = MessagesType(type).GetField("StatusCodes", BindingFlags.Public | BindingFlags.Static);

        Assert.True(field is not null, $"{type} has no StatusCodes");

        return (IReadOnlyDictionary<string, int>)field.GetValue(null)!;
    }

    private static Type MessagesType(string name) =>
        typeof(ScopeMessageMap).Assembly.GetType($"{typeof(ScopeMessageMap).Namespace}.{name}")
        ?? throw new InvalidOperationException($"{name} is not in {typeof(ScopeMessageMap).Namespace}");

    /// <summary>Every routed action, by the operation id SwaggerConfiguration gives it.</summary>
    private static IEnumerable<(string OperationId, MethodInfo Action)> Actions() =>
        typeof(ScopeController).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(ControllerBase)) && !type.IsAbstract)
            .SelectMany(type => type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any())
                .Select(method => ($"{type.Name[..^"Controller".Length]}_{method.Name}", method)));

    /// <summary>
    ///     Whether MVC binds an empty body to this parameter instead of refusing the request: it says so
    ///     (<c>EmptyBodyBehavior.Allow</c>), or it is nullable or has a default value. Anything else is
    ///     refused with 400 by MalformedRequestFilter.
    /// </summary>
    private static bool AcceptsEmptyBody(ParameterInfo parameter) =>
        parameter.GetCustomAttribute<FromBodyAttribute>()!.EmptyBodyBehavior switch
        {
            EmptyBodyBehavior.Allow => true,
            EmptyBodyBehavior.Disallow => false,
            _ => parameter.HasDefaultValue
                 || new NullabilityInfoContext().Create(parameter).WriteState == NullabilityState.Nullable
        };

    private static HashSet<int> SuccessStatuses(JsonElement operation) =>
    [
        .. operation.GetProperty("responses").EnumerateObject()
            .Select(response => int.Parse(response.Name))
            .Where(status => status is >= 200 and < 300)
    ];

    private static bool IsNullable(JsonElement schema) =>
        schema.TryGetProperty("nullable", out var nullable) && nullable.GetBoolean();

    private static JsonElement Property(JsonDocument document, string name)
    {
        var separator = name.IndexOf('.');

        return Schemas(document)
            .Single(schema => schema.Name == name[..separator])
            .Value.GetProperty("properties")
            .GetProperty(name[(separator + 1)..]);
    }

    private static IEnumerable<(string Name, JsonElement Value)> Schemas(JsonDocument document) =>
        document.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject()
            .Select(schema => (schema.Name, schema.Value));

    private static IEnumerable<(string Name, JsonElement Value)> Properties(JsonElement schema) =>
        schema.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Select(property => (property.Name, property.Value))
            : [];

    private static IEnumerable<(string Route, string Method, string OperationId, JsonElement Operation)>
        Operations(JsonDocument document)
    {
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject()
                         .Where(candidate => s_httpMethods.Contains(candidate.Name)))
            {
                yield return (path.Name, operation.Name.ToUpperInvariant(),
                    operation.Value.GetProperty("operationId").GetString()!, operation.Value);
            }
        }
    }

    private static JsonDocument LoadDocument() =>
        JsonDocument.Parse(File.ReadAllText(
            Environment.GetEnvironmentVariable("HEIMDALL_OPENAPI_DOCUMENT")
            ?? Path.Combine(Repository.Root, "docs", "openapi", "heimdall.json")));
}
