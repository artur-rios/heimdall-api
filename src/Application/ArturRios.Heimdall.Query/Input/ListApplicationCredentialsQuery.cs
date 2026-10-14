using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>Lists an application's credentials (UC-54, FR-AP-12). Every field is set by the controller.</summary>
public class ListApplicationCredentialsQuery : BaseQuery, IActorScoped
{
    public Guid ScopeId { get; set; }

    public Guid Id { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
