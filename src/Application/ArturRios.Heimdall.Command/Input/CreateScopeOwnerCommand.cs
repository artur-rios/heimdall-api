using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to create a brand-new <c>ScopeAdmin</c> person directly as a co-owner of a scope
///     (UC-06 path c, FR-SC-12). <see cref="ScopeId" /> comes from the route;
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller (for the AF-06e ownership check) and are never bound from the body. They
///     are <c>[JsonIgnore]</c>, so they are not deserialized from the body and do not appear in the
///     request schema.
/// </summary>
public class CreateScopeOwnerCommand : BaseCommand, IActorScoped
{
    [JsonIgnore]
    public Guid ScopeId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
