namespace ArturRios.Heimdall.Domain.Persistence;

/// <summary>The rows a hard-deleted scope takes with it, by internal id (UC-05 step 4).</summary>
public sealed record ScopeMemberIds(
    IReadOnlyCollection<long> ApplicationIds,
    IReadOnlyCollection<long> GoogleUserIds,
    IReadOnlyCollection<long> PersonIds);

/// <summary>
///     The writes a scope's deletion cascades to its members (UC-04, UC-05), as set-based statements
///     that never load a member, run inside the caller's <see cref="IWriteTransaction" /> (#204).
/// </summary>
/// <remarks>
///     Loading every member to change a flag on each meant a tenant's whole membership — password hashes
///     included — tracked in memory inside one request, growing with the tenant. A statement per kind
///     of member keeps the request's memory flat, whatever the scope's size. The statements bypass the
///     change tracker and so write every column the tracked saves used to — the deletion's time and
///     kind, and <c>UpdatedAt</c> — themselves.
/// </remarks>
public interface IScopeCascades
{
    /// <summary>
    ///     UC-04 step 4: marks every User (via <c>SCOPE_USER</c>), Google User and application of the scope
    ///     that is not yet deleted as deleted at <paramref name="at" />, a deletion of kind
    ///     <c>Administrative</c> for the identities (NFR-20). Members already deleted keep the time and kind
    ///     of their own deletion. Scope owners are not touched.
    /// </summary>
    Task MarkMembersDeletedAsync(long scopeId, DateTime at);

    /// <summary>
    ///     UC-05 step 4: permanently deletes <paramref name="members" /> — applications, then Google Users,
    ///     then persons, so no foreign key is violated — a bounded batch of ids per statement.
    /// </summary>
    Task DeleteMembersAsync(ScopeMemberIds members);
}
