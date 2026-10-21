using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Data.Persistence;

/// <summary>
///     <see cref="IInvariantLocks" /> on PostgreSQL: <c>SELECT 1 FROM heimdall.scope WHERE id = ANY(…)
///     ORDER BY id FOR UPDATE</c>, released when the transaction ends.
/// </summary>
/// <remarks>
///     <para>
///         The scope rows are the natural thing to serialise NFR-12 on: every owner change names its
///         scope, and <see cref="ApplicationCredentialSlots" /> already serialises on its parent row the
///         same way.
///     </para>
///     <para>
///         The runtime login (NFR-29) holds <c>UPDATE</c> on <c>heimdall.scope</c>, which is what
///         <c>FOR UPDATE</c> needs, so the lock needs no grant the login lacks.
///     </para>
/// </remarks>
public sealed class InvariantLocks(AppDbContext context) : IInvariantLocks
{
    public async Task LockAsync(IReadOnlyCollection<long> scopeIds)
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "An invariant lock is taken inside the command's transaction; outside one it ends with its statement.");
        }

        if (scopeIds.Count == 0)
        {
            return;
        }

        // In id order, in one statement: the order IInvariantLocks documents. The table name comes from
        // the model, never from input, so building it into the SQL is safe; the ids stay a parameter.
        var scopeTable = context.Model.FindEntityType(typeof(Scope))!.GetSchemaQualifiedTableName();
        var ids = scopeIds.Distinct().Order().ToArray();

        var sql = $"SELECT 1 FROM {scopeTable} WHERE id = ANY({{0}}) ORDER BY id FOR UPDATE";
        await context.Database.ExecuteSqlRawAsync(sql, ids);
    }
}
