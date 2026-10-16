using ArturRios.Heimdall.Data.Persistence;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for the per-address budget of registration-attempt notices (FR-SR-14) against
// PostgreSQL: the charge is one INSERT … ON CONFLICT … DO UPDATE … WHERE, so what matters is what the
// database does with it — the first charge creating the row, the cap holding, an ended window starting
// again, and a burst of simultaneous charges admitting exactly as many as the budget has left.
[Collection(nameof(FunctionalCollection))]
public class AttemptNoticeBudgetTests(PostgresFixture db)
{
    private static string UniqueHash() => AttemptNoticeBudget.AddressHashOf($"{Guid.NewGuid():N}@test.local");

    private async Task<bool> ChargeAsync(string addressHash, DateTime now)
    {
        await using var context = db.CreateContext();

        return await new AttemptNoticeBudgets(context).TryChargeAsync(
            addressHash, AttemptNoticeBudget.MaxNotices, now, now + AttemptNoticeBudget.Window);
    }

    private async Task<AttemptNoticeBudget> BudgetAsync(string addressHash)
    {
        await using var context = db.CreateContext();

        return await context.AttemptNoticeBudgets.AsNoTracking().SingleAsync(x => x.AddressHash == addressHash);
    }

    [FunctionalFact]
    public async Task GivenAnAddressNeverCharged_WhenChargedFourTimes_ThenThreeAreAdmittedAndOneRowHoldsThem()
    {
        // Given
        var hash = UniqueHash();
        var now = DateTime.UtcNow;

        // When
        var admitted = new List<bool>();

        for (var charge = 1; charge <= 4; charge++)
        {
            admitted.Add(await ChargeAsync(hash, now));
        }

        // Then
        Assert.Equal([true, true, true, false], admitted);

        var budget = await BudgetAsync(hash);
        Assert.Equal(3, budget.NoticesSent);
        Assert.Equal(now + AttemptNoticeBudget.Window, budget.WindowEndsAt, TimeSpan.FromMilliseconds(1));
    }

    [FunctionalFact]
    public async Task GivenASpentBudgetWhoseWindowHasEnded_WhenCharged_ThenANewWindowStartsWithThisNotice()
    {
        // Given a budget spent a day ago
        var hash = UniqueHash();
        var dayAgo = DateTime.UtcNow - AttemptNoticeBudget.Window - TimeSpan.FromMinutes(1);

        for (var charge = 1; charge <= 3; charge++)
        {
            Assert.True(await ChargeAsync(hash, dayAgo));
        }

        // When
        var now = DateTime.UtcNow;
        var admitted = await ChargeAsync(hash, now);

        // Then — one notice in a window that ends a day from now
        Assert.True(admitted);

        var budget = await BudgetAsync(hash);
        Assert.Equal(1, budget.NoticesSent);
        Assert.Equal(now + AttemptNoticeBudget.Window, budget.WindowEndsAt, TimeSpan.FromMilliseconds(1));
    }

    [FunctionalFact]
    public async Task GivenASpentBudgetInAnOpenWindow_WhenCharged_ThenItIsRefusedAndTheWindowIsNotMoved()
    {
        // Given
        var hash = UniqueHash();
        var opened = DateTime.UtcNow.AddHours(-1);

        for (var charge = 1; charge <= 3; charge++)
        {
            Assert.True(await ChargeAsync(hash, opened));
        }

        // When
        var admitted = await ChargeAsync(hash, DateTime.UtcNow);

        // Then — refused, and the window still ends a day after it opened, not a day after this
        Assert.False(admitted);
        Assert.Equal(opened + AttemptNoticeBudget.Window, (await BudgetAsync(hash)).WindowEndsAt,
            TimeSpan.FromMilliseconds(1));
    }

    [FunctionalFact]
    public async Task GivenTenSimultaneousChargesForANewAddress_WhenCharged_ThenExactlyThreeAreAdmitted()
    {
        // Given ten charges released together, each on its own connection — the first notice inserts
        // the row, so the race includes the insert as well as the updates
        var hash = UniqueHash();
        var now = DateTime.UtcNow;
        using var start = new ManualResetEventSlim(false);

        var charges = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () =>
            {
                start.Wait();
                return await ChargeAsync(hash, now);
            }))
            .ToList();

        // When
        start.Set();
        var admitted = await Task.WhenAll(charges);

        // Then
        Assert.Equal(3, admitted.Count(x => x));
        Assert.Equal(3, (await BudgetAsync(hash)).NoticesSent);
    }
}
