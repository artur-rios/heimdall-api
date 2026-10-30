using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Shared.Services;

/// <summary>
///     FR-PE-19: which of another person's scope ownerships a reader may see. A System Admin sees
///     every one; anyone else sees only the ownerships of scopes they own themselves.
/// </summary>
/// <remarks>
///     <para>
///         A Scope Admin can read a co-owner, and the owners of their scopes, because they share a
///         scope with them. That shared scope is what they are entitled to know about. The other
///         scopes the person owns belong to other tenants, so a projection a Scope Admin reads about
///         somebody else reports only the ownerships the two have in common.
///     </para>
///     <para>
///         A person reading their own record sees every scope they own either way, since every
///         scope they own is one of theirs. The callers that short-circuit a self read do so only to
///         save the query.
///     </para>
/// </remarks>
public static class OwnedScopeVisibility
{
    /// <summary>
    ///     The internal ids of the scopes whose ownerships <paramref name="actingPersonId" /> may see
    ///     on another person, or <c>null</c> when they may see all of them (a System Admin).
    /// </summary>
    /// <remarks>
    ///     A logically deleted actor owns nothing, as <see cref="ScopeOwnershipChecker" /> holds: they
    ///     can no longer authenticate, so a token issued before the deletion reveals nothing.
    /// </remarks>
    public static async Task<IReadOnlyCollection<long>?> ScopesVisibleToAsync(
        IAsyncReadOnlyRepository<Person, long> personReader,
        int actingRole,
        Guid actingPersonId,
        CancellationToken cancellationToken = default)
    {
        if (actingRole == (int)Roles.SystemAdmin)
        {
            return null;
        }

        return await personReader.Query()
            .Where(x => x.PublicId == actingPersonId && !x.IsDeleted)
            .SelectMany(x => x.ScopeOwnerships.Select(ownership => ownership.ScopeId))
            .ToListAsync(cancellationToken);
    }
}
