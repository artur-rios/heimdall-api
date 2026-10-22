using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace ArturRios.Heimdall.Data.Tests;

// Functional test of the migration chain itself: every migration applies, every Down runs, and the chain
// applies again from the empty database. Nothing else runs a Down, so one that had rotted (a dropped table
// it no longer matches, a constraint named differently by a later migration) would be found only during
// the rollback it exists for. It runs on a Testcontainers PostgreSQL, never on a developer's database.
//
// The superuser connects, not the owner role the deployment uses: this test is about the migrations' own
// SQL, and the role setup (scripts/sql) is exercised by the WebApi.Tests fixture.
public class MigrationsRoundTripTests
{
    // The version the deployed environments run, as in the WebApi.Tests fixture.
    private const string Image = "postgres:18-alpine";

    // Migrating to this name reverts every migration (EF Core's Migration.InitialDatabase).
    private const string Zero = Migration.InitialDatabase;

    private static AppDbContext CreateContext(string connectionString) => new(
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options,
        NullLoggerFactory.Instance,
        DbContextDiagnosticsOptions.Disabled);

    [FunctionalFact]
    public async Task GivenAnEmptyDatabase_WhenEveryMigrationIsRevertedAndAppliedAgain_ThenEachStepRuns()
    {
        // Given a PostgreSQL container nothing else uses
        await using var container = new PostgreSqlBuilder(Image).Build();
        await container.StartAsync(TestContext.Current.CancellationToken);

        await using var context = CreateContext(container.GetConnectionString());
        var migrator = context.GetService<IMigrator>();
        var all = context.Database.GetMigrations().ToList();

        Assert.NotEmpty(all);

        // When migrated up to the latest — the chain as a deployment applies it
        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(all, (await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).ToList());

        // And every Down is run, newest first, back to the empty database
        await migrator.MigrateAsync(Zero, TestContext.Current.CancellationToken);

        Assert.Empty(await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));

        // Then the chain applies again from nothing, so no Down left something behind that an Up trips on
        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(all, (await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).ToList());
    }
}
