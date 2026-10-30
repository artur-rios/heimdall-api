using ArturRios.Data.Relational.Core.Entities;
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Data.Persistence;

/// <summary>
///     <see cref="IScopeCascades" /> on EF Core's <c>ExecuteUpdate</c> and <c>ExecuteDelete</c>: one
///     <c>UPDATE … WHERE NOT is_deleted</c> per kind of member for a logical delete, and
///     <c>DELETE … WHERE id = ANY(…)</c> per batch of ids for a hard one.
/// </summary>
/// <remarks>
///     <para>
///         Nothing in the context intercepts a save to stamp these rows: <c>UpdatedAt</c> and the
///         deletion columns were always set by the handler before a tracked save, and the one hook the
///         base context has — the concurrency stamp of a versioned entity — applies to none of these
///         types. So the statements set exactly what the tracked saves did. The database's own
///         triggers on these tables (the public-id guards) fire for set-based statements as for any
///         other.
///     </para>
///     <para>
///         A hard delete goes by the ids its caller tombstoned, not by the scope, so exactly the rows
///         with a tombstone are removed. Each statement carries at most <see cref="BatchSize" /> of
///         them, so neither a parameter nor a statement grows with the tenant; the rows referencing them
///         (<c>SCOPE_USER</c>, tokens, two-factor rows, credentials) go by their foreign keys'
///         <c>ON DELETE CASCADE</c>.
///     </para>
/// </remarks>
public sealed class ScopeCascades(AppDbContext context) : IScopeCascades
{
    /// <summary>How many ids one hard-delete statement carries.</summary>
    public const int BatchSize = 2000;

    public async Task MarkMembersDeletedAsync(long scopeId, DateTime at)
    {
        const int administrative = (int)DeletionKinds.Administrative;

        await context.Persons
            .Where(x => !x.IsDeleted && context.ScopeUsers.Any(member => member.PersonId == x.Id && member.ScopeId == scopeId))
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, at)
                .SetProperty(x => x.DeletionKind, administrative)
                .SetProperty(x => x.UpdatedAt, at));

        await context.GoogleUsers
            .Where(x => x.ScopeId == scopeId && !x.IsDeleted)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, at)
                .SetProperty(x => x.DeletionKind, administrative)
                .SetProperty(x => x.UpdatedAt, at));

        await context.Applications
            .Where(x => x.ScopeId == scopeId && !x.IsDeleted)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.UpdatedAt, at));
    }

    public async Task DeleteMembersAsync(ScopeMemberIds members)
    {
        await DeleteInBatchesAsync(context.Applications, members.ApplicationIds);
        await DeleteInBatchesAsync(context.GoogleUsers, members.GoogleUserIds);
        await DeleteInBatchesAsync(context.Persons, members.PersonIds);
    }

    private static async Task DeleteInBatchesAsync<T>(DbSet<T> set, IReadOnlyCollection<long> ids)
        where T : Entity<long>
    {
        foreach (var batch in ids.Chunk(BatchSize))
        {
            await set.Where(x => batch.Contains(x.Id)).ExecuteDeleteAsync();
        }
    }
}
