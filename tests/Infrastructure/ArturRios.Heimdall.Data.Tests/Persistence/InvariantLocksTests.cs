using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Data.Persistence;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.Data.Tests.Persistence;

// A row lock or a transaction-scoped advisory lock taken outside a transaction ends with its own statement, so the
// invariant it was taken to hold (NFR-12, NFR-32) would be held by nothing. InvariantLocks refuses before it sends
// anything: the context below points at a port nothing listens on, and no connection is ever opened.
public class InvariantLocksTests
{
    private static AppDbContext UnconnectedContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x")
            .Options,
        NullLoggerFactory.Instance,
        DbContextDiagnosticsOptions.Disabled);

    public static TheoryData<long[], bool> Requests => new()
    {
        { [1, 2], false },
        { [], true },
        { [3], true },
        { [], false }
    };

    [UnitTheory]
    [MemberData(nameof(Requests))]
    public async Task GivenNoTransaction_WhenLocking_ThenItIsRefusedBeforeTheDatabaseIsReached(
        long[] scopeIds, bool systemAdmins)
    {
        // Given
        await using var context = UnconnectedContext();
        var locks = new InvariantLocks(context);

        // When
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => locks.LockAsync(scopeIds, systemAdmins));

        // Then
        Assert.Contains("inside the command's transaction", refusal.Message);
    }
}
