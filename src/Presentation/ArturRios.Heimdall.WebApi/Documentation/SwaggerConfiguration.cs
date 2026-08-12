using System.Reflection;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ArturRios.Heimdall.WebApi.Documentation;

/// <summary>
///     The OpenAPI document's title, security, and per-operation documentation.
///
///     Applied in two places, which is the point of it living here rather than in either of them.
///     <c>Startup</c> layers it over the SwaggerGen that ArturRios.Util.WebApi registers, so the
///     running API's Swagger UI shows the controllers' own summaries and marks which endpoints need a
///     token. tools/ArturRios.Heimdall.OpenApiGen applies the same method to produce
///     docs/openapi/heimdall.json, the document the documentation site publishes. One definition, so
///     the published page and the running API cannot describe the same endpoint differently.
/// </summary>
public static class SwaggerConfiguration
{
    /// <summary>The document name Util.WebApi registers, and the one served at /swagger/v1/…</summary>
    public const string DocumentName = "v1";

    private const string BearerScheme = "Bearer";

    public static void Configure(SwaggerGenOptions options)
    {
        options.SwaggerDoc(DocumentName, new OpenApiInfo
        {
            Title = "Heimdall API",
            Version = "v1",
            Description =
                "A centralized identity management API with scope-based multi-tenancy.\n\n"
                + "Every identifier in a path is a **PublicId** (GUID), never an internal id. Every "
                + "endpoint answers one of two envelopes — `DataOutput<T>` for a single resource, "
                + "`PaginatedOutput<T>` for a listing.\n\n"
                + "Obtain a token from `POST /api/auth/login`, then authorize with it. A challenge "
                + "token issued by a 2FA-gated login is rejected everywhere except "
                + "`POST /api/auth/2fa/verify`.",
            Contact = new OpenApiContact
            {
                Name = "Artur Rios",
                Url = new Uri("https://github.com/artur-rios/heimdall-api")
            },
            License = new OpenApiLicense
            {
                Name = "MIT",
                Url = new Uri("https://github.com/artur-rios/heimdall-api/blob/main/LICENSE")
            }
        });

        // Assigned rather than added through AddSecurityDefinition, which throws on a duplicate key.
        // Util.WebApi's UseSwaggerGen(jwtAuthentication: true) has already defined "Bearer" by the
        // time this runs inside the API, and has not when the generator runs it — so the one call
        // that works in both places is the one that overwrites instead of insisting on being first.
        options.SwaggerGeneratorOptions.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Paste the token returned by POST /api/auth/login. The 'Bearer ' prefix is added for you."
        };

        // The XML documentation on the controllers is the whole reason the page is worth reading,
        // so a missing file is a failure rather than something to shrug off.
        var xmlPath = Path.Combine(
            AppContext.BaseDirectory,
            $"{typeof(SwaggerConfiguration).Assembly.GetName().Name}.xml");

        if (!File.Exists(xmlPath))
        {
            throw new FileNotFoundException(
                $"The Web API's XML documentation file was not found at {xmlPath}. It is produced by "
                + "<GenerateDocumentationFile> in ArturRios.Heimdall.WebApi.csproj — check that it is "
                + "still set, and that the project was rebuilt after it.",
                xmlPath);
        }

        options.IncludeXmlComments(xmlPath);

        options.DocumentFilter<SecurityDocumentFilter>();

        // Runs last, and must: it normalises text the filters above also write.
        options.DocumentFilter<LineEndingDocumentFilter>();
        options.CustomOperationIds(description =>
            (description.ActionDescriptor as ControllerActionDescriptor)
            is { } action
                ? $"{action.ControllerName}_{action.ActionName}"
                : null);
    }
}

/// <summary>
///     Marks each operation with the security it actually requires, and documents the rejections
///     that security produces.
///
///     Authentication here is Heimdall's own — <c>AllowAnonymousAttribute</c> and
///     <c>RoleRequirementAttribute</c> from ArturRios.Util.WebApi, not ASP.NET Core's — so
///     Swashbuckle's built-in handling of <c>[Authorize]</c> sees nothing and every operation would
///     otherwise be documented as open. The default is the reverse of ASP.NET Core's: an operation is
///     authenticated unless it opts out, which is how the API behaves.
///
///     This is a document filter rather than an operation filter because a security requirement is a
///     reference to a scheme, and a reference only serializes once it is bound to the document that
///     defines the scheme. An operation filter never sees that document, and the requirement
///     serializes as an empty object.
/// </summary>
public sealed class SecurityDocumentFilter : IDocumentFilter
{
    private const string BearerScheme = "Bearer";

    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        // CustomOperationIds gives every action a Controller_Action id, which is what ties an
        // operation in the finished document back to the method it came from.
        var methods = context.ApiDescriptions
            .Where(description => description.ActionDescriptor is ControllerActionDescriptor)
            .ToDictionary(
                description =>
                {
                    var action = (ControllerActionDescriptor)description.ActionDescriptor;

                    return $"{action.ControllerName}_{action.ActionName}";
                },
                description => ((ControllerActionDescriptor)description.ActionDescriptor).MethodInfo);

        // The document-wide default: authenticated unless an operation says otherwise. Util.WebApi
        // sets one of these too when it runs inside the API; assigning rather than appending keeps a
        // single requirement either way, so the API's document and the generated one stay the same.
        document.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(BearerScheme, document)] = []
            }
        ];

        foreach (var operation in document.Paths.Values.SelectMany(path =>
                     path.Operations?.Values ?? Enumerable.Empty<OpenApiOperation>()))
        {
            if (operation.OperationId is null || !methods.TryGetValue(operation.OperationId, out var method))
            {
                continue;
            }

            Apply(document, operation, method);
        }
    }

    private static void Apply(OpenApiDocument document, OpenApiOperation operation, MethodInfo method)
    {
        var attributes = method
            .GetCustomAttributes(inherit: true)
            .Concat(method.DeclaringType?.GetCustomAttributes(inherit: true) ?? [])
            .ToList();

        if (attributes.OfType<AllowAnonymousAttribute>().Any())
        {
            // An empty requirement list, not an absent one. The document carries a default that
            // applies to every operation which does not state its own, so saying nothing here would
            // document an anonymous endpoint as needing a token; an empty list is how OpenAPI spells
            // "this one overrides the default with nothing".
            operation.Security = [];

            operation.Description = Append(operation.Description, "**Anonymous** — no bearer token required.");

            return;
        }

        operation.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(BearerScheme, document)] = []
            }
        ];

        operation.Responses ??= new OpenApiResponses();

        operation.Responses.TryAdd(
            "401",
            new OpenApiResponse { Description = "The token is missing, malformed, expired, or is a 2FA challenge token." });

        var roles = attributes
            .OfType<RoleRequirementAttribute>()
            .SelectMany(RoleNames)
            .Distinct()
            .ToList();

        if (roles.Count == 0)
        {
            operation.Description = Append(
                operation.Description,
                "**Any authenticated caller** — the handler decides who may act, so no role is required at the door.");

            return;
        }

        operation.Responses.TryAdd(
            "403",
            new OpenApiResponse { Description = "The token is valid but the caller does not hold a required role." });

        operation.Description = Append(operation.Description, $"**Requires role:** {string.Join(" or ", roles)}.");
    }

    /// <summary>
    ///     Reads the role ids off a <c>RoleRequirementAttribute</c> and names them.
    ///
    ///     The attribute is a <c>TypeFilterAttribute</c>: it carries no role property of its own, and
    ///     passes the ids to its filter through <c>Arguments</c>. An attribute that stops carrying
    ///     them yields nothing, and the operation is documented without a role line — the security
    ///     requirement above, which is what actually matters, does not depend on this.
    /// </summary>
    private static IEnumerable<string> RoleNames(RoleRequirementAttribute attribute) =>
        (attribute.Arguments ?? [])
        .OfType<IEnumerable<int>>()
        .SelectMany(ids => ids)
        .Select(id => Enum.IsDefined(typeof(Roles), id)
            ? SplitPascalCase(((Roles)id).ToString())
            : $"role {id}");

    private static string SplitPascalCase(string value) =>
        string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $" {character}" : $"{character}"));

    private static string Append(string? description, string line) =>
        string.IsNullOrWhiteSpace(description) ? line : $"{description}\n\n{line}";
}

/// <summary>
///     Rewrites every CRLF in the finished document to LF.
///
///     Swashbuckle builds an operation's summary from the XML documentation file and rejoins the
///     lines with <see cref="Environment.NewLine" />, so a single source comment becomes a summary
///     broken by CRLF on Windows and by LF on Linux. Normalising the XML file on the way in does not
///     help: the newlines are put back after it is read.
///
///     Nobody reading the page can tell the difference, but scripts/openapi.py --check compares
///     bytes — so without this, the document generated on a developer's Windows machine and the one
///     CI regenerates on Linux describe the very same API and still differ, in 46 hunks of pure line
///     ending. LF is the side to standardise on: it is what .gitattributes pins the committed file
///     to.
/// </summary>
public sealed class LineEndingDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        if (document.Info is { } info)
        {
            info.Title = Normalize(info.Title);
            info.Description = Normalize(info.Description);
            info.Summary = Normalize(info.Summary);
        }

        foreach (var tag in document.Tags ?? Enumerable.Empty<OpenApiTag>())
        {
            if (tag is OpenApiTag concrete)
            {
                concrete.Description = Normalize(concrete.Description);
            }
        }

        foreach (var scheme in document.Components?.SecuritySchemes?.Values ?? [])
        {
            if (scheme is OpenApiSecurityScheme concrete)
            {
                concrete.Description = Normalize(concrete.Description);
            }
        }

        foreach (var pathItem in document.Paths.Values)
        {
            if (pathItem is OpenApiPathItem concrete)
            {
                concrete.Summary = Normalize(concrete.Summary);
                concrete.Description = Normalize(concrete.Description);
            }

            foreach (var operation in pathItem.Operations?.Values ?? Enumerable.Empty<OpenApiOperation>())
            {
                Apply(operation);
            }
        }

        // Schemas are walked with a seen-set: a $ref'd schema is shared between the operations that
        // use it, and a self-referencing one would otherwise recurse forever.
        var seen = new HashSet<IOpenApiSchema>();

        foreach (var schema in document.Components?.Schemas?.Values ?? [])
        {
            Apply(schema, seen);
        }
    }

    private static void Apply(OpenApiOperation operation)
    {
        operation.Summary = Normalize(operation.Summary);
        operation.Description = Normalize(operation.Description);

        foreach (var parameter in operation.Parameters ?? [])
        {
            if (parameter is OpenApiParameter concrete)
            {
                concrete.Description = Normalize(concrete.Description);
            }
        }

        if (operation.RequestBody is OpenApiRequestBody requestBody)
        {
            requestBody.Description = Normalize(requestBody.Description);
        }

        foreach (var response in operation.Responses?.Values ?? Enumerable.Empty<IOpenApiResponse>())
        {
            if (response is OpenApiResponse concrete)
            {
                concrete.Description = Normalize(concrete.Description);
            }
        }
    }

    private static void Apply(IOpenApiSchema schema, HashSet<IOpenApiSchema> seen)
    {
        if (!seen.Add(schema))
        {
            return;
        }

        if (schema is OpenApiSchema concrete)
        {
            concrete.Title = Normalize(concrete.Title);
            concrete.Description = Normalize(concrete.Description);
        }

        foreach (var property in schema.Properties?.Values ?? [])
        {
            Apply(property, seen);
        }

        if (schema.Items is { } items)
        {
            Apply(items, seen);
        }

        foreach (var composed in (schema.AllOf ?? []).Concat(schema.AnyOf ?? []).Concat(schema.OneOf ?? []))
        {
            Apply(composed, seen);
        }
    }

    private static string? Normalize(string? text) =>
        text?.ReplaceLineEndings("\n");
}
