namespace ArturRios.Heimdall.Domain.Persistence;

/// <summary>
///     The locks that make a guard and the write it permits one step: NFR-12's "a scope keeps an active
///     owner" (#202) and NFR-32's "the system keeps an active System Admin" (#203). Taken inside the
///     handler's <see cref="IWriteTransactions" /> transaction and held until it commits or rolls back.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why.</b> Each guard reads who else is left and then writes. Two requests taking out the two
///         owners of one scope, or the two System Admins, each read the other still there, both write, and
///         nobody is left. A writer that holds the lock before it reads makes the second request wait until
///         the first has committed, and then read what the first wrote.
///     </para>
///     <para>
///         <b>The lock order, for every command.</b> Scope rows first — in ascending id order, in one
///         statement — and then the System Admin set. A command takes its locks once, before its guards
///         read, through one <see cref="LockAsync" /> call that takes them in that order, and never again in
///         the same transaction. So no two transactions can each hold a lock the other is waiting for: there
///         is no cycle, and so no deadlock.
///     </para>
/// </remarks>
public interface IInvariantLocks
{
    /// <summary>
    ///     Locks the rows of <paramref name="scopeIds" /> (<c>SELECT … ORDER BY id FOR UPDATE</c>), then, when
    ///     <paramref name="systemAdmins" /> is set, the System Admin set, waiting for any transaction holding
    ///     either.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     No transaction is open: the lock would end with the statement.
    /// </exception>
    Task LockAsync(IReadOnlyCollection<long> scopeIds, bool systemAdmins = false);
}
