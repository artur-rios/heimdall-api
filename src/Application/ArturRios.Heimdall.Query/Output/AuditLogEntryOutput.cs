using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     One audit-trail entry as stored (UC-48, FR-AT-02). Nothing is joined or inferred: an actor the
///     retention pass has cleared, or that never existed (an anonymous write), is <c>null</c> here too.
/// </summary>
public class AuditLogEntryOutput : QueryOutput
{
    /// <summary>The entry's public id.</summary>
    public Guid Id { get; init; }

    /// <summary>When the entry was recorded (UTC).</summary>
    public DateTime RecordedAt { get; init; }

    /// <summary>The acting person's public id; <c>null</c> for an anonymous write or a cleared attribution.</summary>
    public Guid? ActorId { get; init; }

    /// <summary>The acting person's role value (see <c>Roles</c>); <c>null</c> whenever <see cref="ActorId" /> is.</summary>
    public int? ActorRole { get; init; }

    /// <summary>The command's type name, e.g. <c>CreateScopeCommand</c>.</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>The affected entity's public id; <c>null</c> when none could be resolved, as for every refusal.</summary>
    public Guid? TargetId { get; init; }

    /// <summary>Whether the write completed; <c>false</c> for a refusal.</summary>
    public bool Succeeded { get; init; }

    /// <summary>A refusal's canonical reason; <c>null</c> on success.</summary>
    public string? FailureReason { get; init; }
}
