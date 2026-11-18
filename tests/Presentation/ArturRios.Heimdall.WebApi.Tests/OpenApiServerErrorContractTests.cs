using System.Reflection;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.WebApi.EndpointToggle;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     Holds the document to the refusals Kestrel makes on the API's behalf: those of a request line or
///     headers, before the API sees the request, and those of a request body, which reach
///     ExceptionMiddleware while an action's body binding reads it.
/// </summary>
/// <remarks>
///     Kestrel's rejections are KestrelBadHttpRequestException's table: 400, 408, 414, 431 and 505 while
///     it parses the request line and headers, answered by Kestrel itself with <c>Content-Length: 0</c>;
///     400 (a body cut short or badly chunked), 408 (below the minimum data rate) and 413 (over
///     MaxRequestBodySize) while the body is read, thrown into the API, which reads a body only to bind
///     a <c>[FromBody]</c> parameter. ExceptionMiddleware answers those three with the exception's own
///     status and a <c>DataOutput&lt;string&gt;</c> whose <c>data</c> is empty.
/// </remarks>
public partial class OpenApiResponseContractTests
{
    private static readonly int[] s_requestBodyStatuses = [400, 408, 413];

    private static readonly string[] s_serverStatuses = ["400", "408", "414", "431", "505"];

    private static readonly string[] s_kestrelOnlyStatuses = ["414", "431", "505"];

    private static readonly string[] s_middlewareOnlyBodyStatuses = ["408", "413"];

    [UnitFact]
    public void GivenEveryOperation_WhenRead_ThenItListsKestrelsRefusalsOfTheRequestLineAndHeaders()
    {
        using var document = LoadDocument();

        Assert.All(Operations(document), operation =>
        {
            var responses = operation.Operation.GetProperty("responses");

            Assert.All(s_serverStatuses, status => Assert.True(
                responses.TryGetProperty(status, out _),
                $"{operation.OperationId} does not list Kestrel's {status}"));

            // Only Kestrel answers these, and it answers with an empty body.
            Assert.All(s_kestrelOnlyStatuses, status => Assert.Empty(MediaTypes(responses.GetProperty(status))));
        });
    }

    [UnitFact]
    public void GivenActionsBindingABody_WhenDocumentRead_ThenExactlyTheirOperationsListKestrelsBodyRefusals()
    {
        using var document = LoadDocument();
        var operations = Operations(document).ToDictionary(operation => operation.OperationId);

        var binding = Actions()
            .Where(action => action.Action.GetParameters().Any(parameter => parameter.GetCustomAttribute<FromBodyAttribute>() is not null))
            .Select(action => action.OperationId)
            .Order(StringComparer.Ordinal)
            .ToList();
        var listing413 = operations.Values
            .Where(operation => operation.Operation.GetProperty("responses").TryGetProperty("413", out _))
            .Select(operation => operation.OperationId)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(binding);
        Assert.Equal(binding, listing413);

        foreach (var operationId in binding)
        {
            var responses = operations[operationId].Operation.GetProperty("responses");

            // Written by ExceptionMiddleware alone: DataOutput<string>, as JSON whatever is accepted.
            foreach (var response in s_middlewareOnlyBodyStatuses.Select(status => responses.GetProperty(status)))
            {
                Assert.Equal(["application/json"], MediaTypes(response));
                Assert.Equal("#/components/schemas/StringDataOutput", SchemaReference(response));
            }

            // Joined by MalformedRequestFilter's and the handler's bodies, which only ObjectDataOutput holds with it.
            Assert.Equal("#/components/schemas/ObjectDataOutput", SchemaReference(responses.GetProperty("400")));
        }

        // Elsewhere Kestrel's 408 is its own, for headers sent too slowly, without a body.
        Assert.All(
            operations.Values.Where(operation => !binding.Contains(operation.OperationId)),
            operation => Assert.Empty(MediaTypes(operation.Operation.GetProperty("responses").GetProperty("408"))));
    }

    [UnitFact]
    public void GivenTheApi_WhenItsActionsAndSourceAreRead_ThenNothingCanThrowEndpointDisabledException()
    {
        // ExceptionMiddleware answers EndpointDisabledException with its own status, but only
        // EndpointToggleAttribute throws it, and no action carries one. An action that gains one needs
        // its status documented here first.
        var toggled = Actions()
            .Where(action => action.Action.GetCustomAttribute<EndpointToggleAttribute>(inherit: true) is not null
                             || action.Action.DeclaringType!.GetCustomAttribute<EndpointToggleAttribute>(inherit: true) is not null)
            .Select(action => action.OperationId)
            .ToList();

        var naming = Directory.EnumerateFiles(Repository.PathTo("src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .Where(path =>
            {
                var code = LineComment().Replace(BlockComment().Replace(File.ReadAllText(path), string.Empty), string.Empty);

                return code.Contains(nameof(EndpointDisabledException), StringComparison.Ordinal)
                       || code.Contains("EndpointToggle", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(toggled);
        Assert.Empty(naming);
    }
}
