using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace ArturRios.Heimdall.WebApi.Binding;

/// <summary>
///     Model-binding configuration shared by the running API and the OpenAPI document generator.
///
///     Both call <see cref="Configure" /> for the same reason they both call
///     <c>SwaggerConfiguration.Configure</c>: tools/ArturRios.Heimdall.OpenApiGen builds its own
///     <c>AddControllers()</c> rather than using <c>Startup</c>, so configuration registered in only
///     one of the two would let the published document disagree with the API it documents.
/// </summary>
public static class ModelBindingConfiguration
{
    public static void Configure(MvcOptions options)
    {
        options.ModelMetadataDetailsProviders.Add(new ServerPopulatedBindingMetadataProvider());

        // With nullable reference types on, MVC infers [Required] on every non-nullable string or
        // collection property, so a JSON null would invalidate ModelState and MalformedRequestFilter
        // would answer the generic "The request is malformed." before the handler runs — hiding the
        // field's own message ("Secret is required.") and skipping the refused-write audit entry
        // (NFR-09). FluentValidation owns required-ness; the filter means only "cannot be bound".
        options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
    }
}

/// <summary>
///     Makes a <c>[JsonIgnore]</c> property non-bindable, so a value arriving in the query string
///     cannot reach it and ApiExplorer does not publish it as a parameter.
///
///     The commands and queries the mediator takes as input double as the wire DTOs, so a property
///     the controller assigns from the route or the authenticated caller looks, to the framework,
///     exactly like one the client sends. <c>[JsonIgnore]</c> marks the difference: it already
///     excludes the property from a <c>[FromBody]</c> payload and its schema, and this provider
///     extends the same statement to the <c>[FromQuery]</c> path, where System.Text.Json attributes
///     otherwise mean nothing. <c>IsBindingAllowed = false</c> is what <c>[BindNever]</c> sets;
///     <c>[BindNever]</c> itself is unavailable to the Application-layer projects these DTOs live
///     in, which have no reference to the ASP.NET Core shared framework.
/// </summary>
public class ServerPopulatedBindingMetadataProvider : IBindingMetadataProvider
{
    public void CreateBindingMetadata(BindingMetadataProviderContext context)
    {
        if (context.Attributes.OfType<JsonIgnoreAttribute>().Any())
        {
            context.BindingMetadata.IsBindingAllowed = false;
        }
    }
}
