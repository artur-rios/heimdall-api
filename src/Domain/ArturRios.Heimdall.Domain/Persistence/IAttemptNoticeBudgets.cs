namespace ArturRios.Heimdall.Domain.Persistence;

/// <summary>
///     Charges the per-address budget of registration-attempt notices (FR-SR-14): see
///     <see cref="Entities.AttemptNoticeBudget" />.
/// </summary>
/// <remarks>
///     Its own seam rather than a method of <see cref="IAtomicWrites" />, for the reason
///     <see cref="IErasedSubjects" /> is: the first notice to an address has no row to update, and a
///     plain insert fails on the unique address the second time. One <c>INSERT … ON CONFLICT … DO
///     UPDATE … WHERE</c> lets the database decide, so of any number of simultaneous notices to one
///     address exactly as many are admitted as the budget has left.
/// </remarks>
public interface IAttemptNoticeBudgets
{
    /// <summary>
    ///     Reserves one notice to the address whose digest is <paramref name="addressHash" />, while it
    ///     has been sent fewer than <paramref name="maxNotices" /> in a window that is still open at
    ///     <paramref name="now" />. A window that has ended, or an address never charged, starts a new
    ///     one ending at <paramref name="windowEndsAt" /> with this notice as its first.
    /// </summary>
    /// <returns><c>true</c> when the notice may be sent; <c>false</c> when the budget is spent.</returns>
    Task<bool> TryChargeAsync(string addressHash, int maxNotices, DateTime now, DateTime windowEndsAt);
}
