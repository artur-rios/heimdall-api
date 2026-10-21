using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Domain.Persistence;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArturRios.Heimdall.Data.Persistence;

/// <summary>
///     <see cref="IWriteTransactions" /> on the request's <see cref="AppDbContext" />: a database
///     transaction every write made through that context joins until it is committed or disposed.
/// </summary>
/// <remarks>
///     <para>
///         The context runs without a retrying execution strategy, so a transaction the caller opens
///         is allowed. <see cref="ApplicationCredentialSlots" /> wraps its own in the strategy because
///         it owns the whole unit; here the unit is the handler's and cannot be replayed by EF Core.
///     </para>
///     <para>
///         The rollback and the clearing of the change tracker never throw: they run because something
///         already failed, and must not replace that failure with their own.
///     </para>
/// </remarks>
public sealed class WriteTransactions(AppDbContext context) : IWriteTransactions
{
    public async Task<IWriteTransaction> BeginAsync() =>
        new WriteTransaction(context, await context.Database.BeginTransactionAsync());

    private sealed class WriteTransaction(AppDbContext context, IDbContextTransaction transaction)
        : IWriteTransaction
    {
        private bool _committed;

        public async Task CommitAsync()
        {
            await transaction.CommitAsync();
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch
                {
                    // Already rolled back, or the connection is gone; disposing finishes the cleanup.
                }

                // Nothing the transaction wrote was kept, so nothing the tracker holds about it is true:
                // a failed save's entities are still pending, and the next save would replay them.
                context.ChangeTracker.Clear();
            }

            await transaction.DisposeAsync();
        }
    }
}
