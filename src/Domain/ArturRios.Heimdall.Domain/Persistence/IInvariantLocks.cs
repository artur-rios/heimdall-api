namespace ArturRios.Heimdall.Domain.Persistence;

/// <summary>
///     The locks that make a guard and the write it permits one step: NFR-12's "a scope keeps an active
///     owner" (#202). Taken inside the handler's <see cref="IWriteTransactions" /> transaction and held
///     until it commits or rolls back.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why.</b> Each guard reads who else is left and then writes. Two requests taking out the two
///         owners of one scope each read the other still there, both write, and nobody is left. A writer
///         that holds the lock before it reads makes the second request wait until the first has
///         committed, and then read what the first wrote.
///     </para>
///     <para>
///         <b>The lock order, for every command.</b> The scope rows in ascending id order, in one
///         statement, and only through <see cref="LockAsync" />, so no two transactions can each hold a
///         row the other is waiting for: there is no cycle, and so no deadlock. A command takes its locks
///         once, before its guard reads, and never again in the same transaction.
///     </para>
/// </remarks>
public interface IInvariantLocks
{
    /// <summary>
    ///     Locks the rows of <paramref name="scopeIds" /> (<c>SELECT … ORDER BY id FOR UPDATE</c>), waiting
    ///     for any transaction holding one of them.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open: the lock would end with the statement.</exception>
    Task LockAsync(IReadOnlyCollection<long> scopeIds);
}
