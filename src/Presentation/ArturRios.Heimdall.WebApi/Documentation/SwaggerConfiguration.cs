using System.Reflection;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ArturRios.Heimdall.WebApi.Documentation;

/// <summary>
///     The OpenAPI document's title, security, and per-operation documentation.
///
///     Applied in two places, which is the point of it living here rather than in either of them.
///     <c>Startup</c> hands it to ArturRios.Util.WebApi as the Swagger generator's configuration, so
///     the running API's Swagger UI shows the controllers' own summaries and marks which endpoints
///     need a token. tools/ArturRios.Heimdall.OpenApiGen applies the same method to produce
///     docs/openapi/heimdall.json, the document heimdall-docs publishes. One definition, so
///     the published page and the running API cannot describe the same endpoint differently.
/// </summary>
public static class SwaggerConfiguration
{
    /// <summary>The document name Swagger UI looks for by default, and the one served at /swagger/v1/…</summary>
    public const string DocumentName = "v1";

    /// <summary>
    ///     The OpenAPI version the document is written in, by the running API at /swagger/… and by
    ///     tools/ArturRios.Heimdall.OpenApiGen alike.
    /// </summary>
    /// <remarks>
    ///     3.1, not Swashbuckle's default of 3.0, because 3.1 spells null as JSON Schema does, with a
    ///     <c>"null"</c> type, and that combines with a reference as readily as with a scalar. 3.0's
    ///     <c>nullable</c> only counts beside a <c>type</c>, so a nullable reference has no 3.0
    ///     spelling that a client generator reads as nullable without also inventing a wrapper model
    ///     for it.
    /// </remarks>
    public const OpenApiSpecVersion SpecVersion = OpenApiSpecVersion.OpenApi3_1;

    private const string BearerScheme = "Bearer";

    /// <summary>
    ///     Serves the running API's document in <see cref="SpecVersion" />. Swashbuckle's middleware
    ///     reads its options from the container, so this is registered with <c>ConfigureSwagger</c>
    ///     rather than passed to <c>UseSwagger</c>, which Util.WebApi calls without arguments.
    /// </summary>
    public static void ConfigureEndpoint(SwaggerOptions options) => options.OpenApiVersion = SpecVersion;

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
                + "token issued by a 2FA-gated login is not one: no endpoint accepts it as a bearer "
                + "credential, and it is read only from the `challengeToken` body field of "
                + "`POST /api/auth/2fa/verify` and `POST /api/auth/2fa/challenge/resend`.",
            Contact = new OpenApiContact
            {
                Name = "Artur Rios",
                Url = new Uri("https://github.com/artur-rios/heimdall-api")
            },
            // The repository's own LICENSE, by the name its first line gives it: the Artur Rios
            // Attribution License has no SPDX identifier, so the name and URL stand for it.
            License = new OpenApiLicense
            {
                Name = "Artur Rios Attribution License",
                Url = new Uri("https://github.com/artur-rios/heimdall-api/blob/main/LICENSE")
            }
        });

        // The only definition of "Bearer" in either place: Startup leaves Util.WebApi's JWT scheme
        // off, because AddSecurityDefinition throws on a duplicate key and the library's runs after
        // this method. Assigned rather than added all the same, so a second definition overwrites
        // this one instead of failing start-up.
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

        options.RequestBodyFilter<RequiredRequestBodyFilter>();
        options.SchemaFilter<NullableReferencePropertySchemaFilter>();
        // After IncludeXmlComments, whose filter writes the descriptions this one takes back off.
        options.SchemaFilter<ReferenceDescriptionSchemaFilter>();

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

        // The document-wide default: authenticated unless an operation says otherwise. Assigned
        // rather than appended, so it stays the single requirement even if something else adds one.
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

        operation.Responses ??= [];

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
///     Marks a <c>[FromBody]</c> request body as required when the action rejects a request without
///     one.
///
///     MVC refuses an empty body unless the action allows it: <c>[FromBody(EmptyBodyBehavior =
///     EmptyBodyBehavior.Allow)]</c>, a nullable parameter, a parameter with a default value, or
///     <c>MvcOptions.AllowEmptyInputInBodyModelBinding</c>. The refusal is a model-state error, which
///     <c>MalformedRequestFilter</c> answers with 400 before the handler runs. Swashbuckle documents
///     every body as optional all the same, because the API turns off MVC's implicit
///     <c>[Required]</c> (<c>ModelBindingConfiguration</c>) and that is what it reads; so this filter
///     states the binder's own rule instead. A generated client builds a method without a body, or
///     with an optional one, from an optional body, which is a call the server can only refuse.
/// </summary>
public sealed class RequiredRequestBodyFilter(IOptions<MvcOptions> mvcOptions) : IRequestBodyFilter
{
    public void Apply(IOpenApiRequestBody requestBody, RequestBodyFilterContext context)
    {
        if (requestBody is not OpenApiRequestBody concrete
            || context.BodyParameterDescription?.ParameterDescriptor is not { } parameter)
        {
            return;
        }

        concrete.Required = !AllowsEmptyBody(parameter);
    }

    private bool AllowsEmptyBody(ParameterDescriptor parameter)
    {
        var behavior = parameter.BindingInfo?.EmptyBodyBehavior ?? EmptyBodyBehavior.Default;

        if (behavior != EmptyBodyBehavior.Default)
        {
            return behavior == EmptyBodyBehavior.Allow;
        }

        // What MVC infers for an unattributed parameter: a nullable one, or one with a default
        // value, takes an empty body as "no value".
        if (parameter is ControllerParameterDescriptor { ParameterInfo: var info }
            && (info.HasDefaultValue
                || new NullabilityInfoContext().Create(info).WriteState == NullabilityState.Nullable))
        {
            return true;
        }

        return mvcOptions.Value.AllowEmptyInputInBodyModelBinding;
    }
}

/// <summary>
///     Marks a property whose schema is a reference to another schema as nullable when the C# member
///     is declared nullable.
///
///     The API writes nulls, so <c>"data": null</c> is on the wire whenever an envelope carries no
///     payload (<c>DataOutput&lt;T&gt;.Data</c> is <c>T?</c>), and so is an optional section such as
///     <c>DataExportCommandOutput.Erasure</c>. Swashbuckle already marks every other reference-typed
///     property nullable — in OpenAPI 3.1, a <c>"null"</c> in its <c>type</c> — but a reference has
///     no <c>type</c> of its own to add it to, so a property that refers to another schema is left
///     looking as if it can never be null, and a client generated from the document fails on the
///     first null it reads. The 3.1 spelling is the reference or null:
///     <c>anyOf: [{ $ref }, { type: "null" }]</c>.
///
///     Only a member the nullable annotations declare nullable is wrapped: a <c>required</c> or
///     otherwise non-nullable one keeps its plain reference, because that is a promise the code
///     makes.
/// </summary>
public sealed class NullableReferencePropertySchemaFilter(ISerializerDataContractResolver resolver) : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        // Only a type's own schema: a member's schema is a reference already, or inline.
        if (context.MemberInfo is not null
            || context.ParameterInfo is not null
            || schema.Properties is not { Count: > 0 } properties
            || resolver.GetDataContractForType(context.Type).ObjectProperties is not { } members)
        {
            return;
        }

        var nullability = new NullabilityInfoContext();

        foreach (var member in members)
        {
            if (properties.TryGetValue(member.Name, out var property)
                && property is OpenApiSchemaReference reference
                && IsNullable(nullability, member.MemberInfo))
            {
                properties[member.Name] = new OpenApiSchema
                {
                    AnyOf = [reference, new OpenApiSchema { Type = JsonSchemaType.Null }]
                };
            }
        }
    }

    private static bool IsNullable(NullabilityInfoContext nullability, MemberInfo? member) =>
        member switch
        {
            PropertyInfo property => nullability.Create(property).ReadState != NullabilityState.NotNull,
            FieldInfo field => nullability.Create(field).ReadState != NullabilityState.NotNull,
            _ => false
        };
}

/// <summary>
///     Leaves a bare reference to a type's schema without a description of its own.
///
///     Swashbuckle's XML comments filter gives a type's summary to every schema it builds for the
///     type, a bare reference to it included: an array's items, a response body. OpenAPI 3.0 ignores
///     anything beside a <c>$ref</c>, so the copy never reached the document; 3.1 keeps it, and it only
///     repeats the description the referenced schema already carries, with the XML file's line
///     endings at that. Taking it off keeps the 3.1 document the 3.0 one in everything but its
///     nullability. A reference built for a member or a parameter is left alone: its description is
///     the member's own documentation, not a copy.
/// </summary>
public sealed class ReferenceDescriptionSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is OpenApiSchemaReference reference
            && context.MemberInfo is null
            && context.ParameterInfo is null)
        {
            reference.Description = null;
        }
    }
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
