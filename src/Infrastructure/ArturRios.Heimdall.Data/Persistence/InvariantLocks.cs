using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Data.Persistence;

/// <summary>
///     <see cref="IInvariantLocks" /> on PostgreSQL: <c>SELECT 1 FROM heimdall.scope WHERE id = ANY(…)
///     ORDER BY id FOR UPDATE</c> for the scopes, then <c>pg_advisory_xact_lock</c> on
///     <see cref="SystemAdminSetKey" /> for the System Admin set, both released when the transaction ends.
/// </summary>
/// <remarks>
///     <para>
///         The scope rows are the natural thing to serialise NFR-12 on: every owner change names its
///         scope, and <see cref="ApplicationCredentialSlots" /> already serialises on its parent row the
///         same way. The System Admin set has no row of its own, so it gets an advisory lock: one key for
///         the whole set, since the rule is about the set (#203).
///     </para>
///     <para>
///         The runtime login (NFR-29) holds <c>UPDATE</c> on <c>heimdall.scope</c>, which is what
///         <c>FOR UPDATE</c> needs, and may call <c>pg_advisory_xact_lock</c>: PUBLIC keeps the
///         <c>EXECUTE</c> it has on <c>pg_catalog</c>'s functions, which <c>runtime-grants.sql</c> never
///         revokes. So neither lock needs a grant the login lacks.
///     </para>
/// </remarks>
public sealed class InvariantLocks(AppDbContext context) : IInvariantLocks
{
    /// <summary>
    ///     The advisory lock key of the System Admin set (NFR-32): <c>"HEIMSYSA"</c> in ASCII, so it is
    ///     recognisable in <c>pg_locks</c>. Nothing else takes an advisory lock in this database.
    /// </summary>
    public const long SystemAdminSetKey = 0x4845494D53595341;

    public async Task LockAsync(IReadOnlyCollection<long> scopeIds, bool systemAdmins = false)
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "An invariant lock is taken inside the command's transaction; outside one it ends with its statement.");
        }

        // The order IInvariantLocks documents: the scope rows, in id order and in one statement, and then
        // the System Admin set.
        if (scopeIds.Count > 0)
        {
            // The table name comes from the model, never from input, so building it into the SQL is safe;
            // the ids stay a parameter.
            var scopeTable = context.Model.FindEntityType(typeof(Scope))!.GetSchemaQualifiedTableName();
            var ids = scopeIds.Distinct().Order().ToArray();

            var sql = $"SELECT 1 FROM {scopeTable} WHERE id = ANY({{0}}) ORDER BY id FOR UPDATE";
            await context.Database.ExecuteSqlRawAsync(sql, ids);
        }

        if (systemAdmins)
        {
            await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", SystemAdminSetKey);
        }
    }
}
