using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request to retrieve a single Google User by its <c>PublicId</c> within a scope (UC-27,
///     FR-GO-14). The pagination members inherited from <see cref="BaseQuery" /> are unused for a
///     by-id lookup. <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the
///     controller from the authenticated caller, for the AF-27b visibility rule.
/// </summary>
public class GetGoogleUserByIdQuery : BaseQuery, IActorScoped
{
    /// <summary>Public identifier of the scope the Google User must belong to (from the route).</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the Google User to retrieve.</summary>
    public Guid Id { get; set; }

    /// <summary>When <c>true</c>, a logically deleted Google User is still returned (FR-GO-17).</summary>
    public bool IncludeDeleted { get; set; }

    /// <summary>
    ///     The acting caller's <c>PublicId</c>. For a Google User this is their own Google User
    ///     <c>PublicId</c> — UC-25 issues the token claiming it in the same position a person's would
    ///     occupy — which is what lets the handler recognise a self-read.
    /// </summary>
    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
