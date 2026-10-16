using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Data.Persistence;

/// <summary>
///     <see cref="IAttemptNoticeBudgets" /> on PostgreSQL: one <c>INSERT … ON CONFLICT … DO UPDATE …
///     WHERE</c> per notice.
/// </summary>
/// <remarks>
///     <para>
///         An address never charged has no row, so the first charge inserts one and every later charge
///         updates it. Reading first and then choosing would race: two first notices would both insert,
///         and two notices at the limit would both read "two sent". The upsert leaves it to the
///         database. A new address is inserted with one notice; a known one is updated only where the
///         <c>WHERE</c> admits it — its window has ended (it starts again at one) or it has had fewer
///         than the maximum — and every right-hand side reads the row as it was.
///     </para>
///     <para>
///         The affected-row count is the answer. A refused update affects no row; an insert or an
///         admitted update affects one. A competing upsert on the same address waits for the first to
///         commit and then evaluates the <c>WHERE</c> against the row as that one left it, as the
///         conditional updates of <see cref="AtomicWrites" /> do, so no burst admits more than the
///         budget has left.
///     </para>
/// </remarks>
public sealed class AttemptNoticeBudgets(AppDbContext context) : IAttemptNoticeBudgets
{
    public async Task<bool> TryChargeAsync(string addressHash, int maxNotices, DateTime now, DateTime windowEndsAt)
    {
        // The table name comes from the model, never from input, so building it into the SQL is safe;
        // the values stay parameters.
        var table = context.Model.FindEntityType(typeof(AttemptNoticeBudget))!.GetSchemaQualifiedTableName();

        var sql =
            $$"""
              INSERT INTO {{table}} AS budget (address_hash, notices_sent, window_ends_at)
              VALUES ({0}, 1, {2})
              ON CONFLICT (address_hash) DO UPDATE SET
                  notices_sent = CASE WHEN budget.window_ends_at <= {1} THEN 1 ELSE budget.notices_sent + 1 END,
                  window_ends_at = CASE WHEN budget.window_ends_at <= {1} THEN EXCLUDED.window_ends_at
                                        ELSE budget.window_ends_at END
              WHERE budget.window_ends_at <= {1} OR budget.notices_sent < {3}
              """;

        return await context.Database.ExecuteSqlRawAsync(sql, addressHash, now, windowEndsAt, maxNotices) == 1;
    }
}
