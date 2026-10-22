using ArturRios.Heimdall.Domain.Persistence;

namespace ArturRios.Heimdall.Command.Tests.Support;

/// <summary>
///     <see cref="IWriteTransactions" /> for handler tests over the fake repositories: it opens nothing and
///     cannot undo what a handler wrote to a fake, but it counts what a handler asked of it, so a test can
///     show that a refused or failed command never commits. The functional tests prove the rollback itself
///     against PostgreSQL.
/// </summary>
public sealed class InMemoryWriteTransactions : IWriteTransactions
{
    /// <summary>Shared by tests that do not count; its counts mean nothing.</summary>
    public static readonly InMemoryWriteTransactions Instance = new();

    private int _begun;
    private int _committed;
    private int _rolledBack;

    public int Begun => _begun;
    public int Committed => _committed;

    /// <summary>Transactions disposed without a commit.</summary>
    public int RolledBack => _rolledBack;

    public Task<IWriteTransaction> BeginAsync()
    {
        Interlocked.Increment(ref _begun);

        return Task.FromResult<IWriteTransaction>(new Transaction(this));
    }

    private sealed class Transaction(InMemoryWriteTransactions owner) : IWriteTransaction
    {
        private bool _committed;

        public Task CommitAsync()
        {
            _committed = true;
            Interlocked.Increment(ref owner._committed);

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                Interlocked.Increment(ref owner._rolledBack);
            }

            return ValueTask.CompletedTask;
        }
    }
}
