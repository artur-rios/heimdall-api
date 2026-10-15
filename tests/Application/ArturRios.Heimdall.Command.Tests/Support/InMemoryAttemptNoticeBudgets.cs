using ArturRios.Heimdall.Domain.Persistence;

namespace ArturRios.Heimdall.Command.Tests.Support;

/// <summary>
///     <see cref="IAttemptNoticeBudgets" /> over a dictionary, applying the same rule the database
///     statement applies: a window that has ended, or an address never charged, starts again at one;
///     otherwise a notice is admitted only while fewer than the maximum have been sent. Racing charges
///     are the functional tests' subject, against PostgreSQL.
/// </summary>
public sealed class InMemoryAttemptNoticeBudgets : IAttemptNoticeBudgets
{
    private readonly Dictionary<string, (int Sent, DateTime WindowEndsAt)> _budgets = new();

    /// <summary>Notices admitted so far, per address digest.</summary>
    public int SentTo(string addressHash) => _budgets.TryGetValue(addressHash, out var budget) ? budget.Sent : 0;

    public Task<bool> TryChargeAsync(string addressHash, int maxNotices, DateTime now, DateTime windowEndsAt)
    {
        if (!_budgets.TryGetValue(addressHash, out var budget) || budget.WindowEndsAt <= now)
        {
            _budgets[addressHash] = (1, windowEndsAt);

            return Task.FromResult(true);
        }

        if (budget.Sent >= maxNotices)
        {
            return Task.FromResult(false);
        }

        _budgets[addressHash] = (budget.Sent + 1, budget.WindowEndsAt);

        return Task.FromResult(true);
    }
}
