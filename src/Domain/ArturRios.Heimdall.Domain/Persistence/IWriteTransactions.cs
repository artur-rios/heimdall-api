namespace ArturRios.Heimdall.Domain.Persistence;

/// <summary>
///     Opens a database transaction around the writes of one command that must land together or not
///     at all: a single-use token spent with the write it unlocks (#209), a hard delete's tombstones,
///     dependents and root (#212), a scope's flag with its cascade (#204).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> Every repository write saves and commits on its own, so a command that
///         makes several of them used to stand on its write order alone: spend the token, then write;
///         tombstone, then delete. A failure in between left the first half — a token gone for a reset
///         that never happened, dependents removed from a person who survived. Inside a transaction
///         opened here, the repositories, <see cref="IAtomicWrites" /> and <see cref="IErasedSubjects" />
///         all write on the one connection, and nothing is kept until
///         <see cref="IWriteTransaction.CommitAsync" />.
///     </para>
///     <para>
///         <b>One per command, opened by the handler.</b> The handler that owns the rule opens the
///         transaction, in one place, around exactly the writes that belong together, so whatever must
///         happen inside it — a row lock, a re-read — has an obvious home. A second transaction cannot
///         be opened while one is open on the same unit of work. Slow work, such as hashing a password,
///         stays outside it, so no row lock is held for longer than the writes take.
///     </para>
/// </remarks>
public interface IWriteTransactions
{
    /// <summary>Opens a transaction on the request's unit of work.</summary>
    Task<IWriteTransaction> BeginAsync();
}

/// <summary>
///     An open transaction: kept by <see cref="CommitAsync" />, and rolled back when disposed without
///     it — whether the command refused, failed or threw.
/// </summary>
/// <remarks>
///     A rollback also forgets everything the unit of work had tracked: entities whose save failed are
///     otherwise still pending, and the next save on it — the audit entry's, which
///     <c>AuditingCommandHandler</c> writes after the handler returns or throws — would try to write
///     them again, outside any transaction, after the rollback had undone the rest. Dispose the
///     transaction (<c>await using</c>) before anything else writes.
/// </remarks>
public interface IWriteTransaction : IAsyncDisposable
{
    /// <summary>Keeps every write made since the transaction was opened.</summary>
    Task CommitAsync();
}
