using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request to list the audit trail (UC-48, FR-AT-01), newest first. Every filter is optional, and
///     those given combine with AND. <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by
///     the controller from the authenticated caller and are never bound from the request.
/// </summary>
/// <remarks>
///     There is no scope filter, and cannot be one: <c>AUDIT_LOG</c> has no scope column, and an old
///     row's target may since have been deleted or anonymised, so it cannot be attributed to a scope
///     reliably. That is why the listing is System Admins' only.
/// </remarks>
public class ListAuditLogQuery : BaseQuery, IActorScoped
{
    /// <summary>Earliest recording instant to include (UTC, inclusive).</summary>
    public DateTime? From { get; set; }

    /// <summary>Latest recording instant to include (UTC, inclusive). Must not be earlier than <see cref="From" />.</summary>
    public DateTime? To { get; set; }

    /// <summary>
    ///     The actor's public id. An entry whose actor has been cleared (NFR-21) names nobody, so this
    ///     filter cannot find it.
    /// </summary>
    public Guid? ActorId { get; set; }

    /// <summary>
    ///     The exact action, as recorded — the command's type name, e.g. <c>CreateScopeCommand</c>.
    ///     Matched case-sensitively; empty or whitespace means no filter.
    /// </summary>
    public string? Action { get; set; }

    /// <summary>The affected entity's public id.</summary>
    public Guid? TargetId { get; set; }

    /// <summary><c>true</c> for completed writes, <c>false</c> for refused ones.</summary>
    public bool? Succeeded { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
