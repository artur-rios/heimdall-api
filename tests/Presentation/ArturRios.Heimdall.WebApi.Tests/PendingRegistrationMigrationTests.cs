using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ArturRios.Heimdall.WebApi.Tests;

// pending_registration.public_id (UC-49, UC-50). The backfill only matters on a database that already
// holds pending registrations, so the first test builds one: a fresh database migrated to just before
// AddPendingRegistrationPublicId, two rows written, then the rest of the migrations applied. The second
// requires the database itself to keep the id unique. Kept apart from SchemaTests so this branch and the
// audit-trail branch do not edit the same file.
[Collection(nameof(FunctionalCollection))]
public class PendingRegistrationMigrationTests(PostgresFixture fixture)
{
    private const string MigrationSuffix = "_AddPendingRegistrationPublicId";

    private static AppDbContext ContextFor(string connectionString) => new(
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options,
        NullLoggerFactory.Instance,
        DbContextDiagnosticsOptions.Disabled);

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<Guid>> ReadPublicIdsAsync(string connectionString)
    {
        var ids = new List<Guid>();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select public_id from heimdall.pending_registration", connection);
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task InsertPendingAsync(string connectionString, Guid publicId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            WITH s AS (
                INSERT INTO heimdall.scope (public_id, name) VALUES (gen_random_uuid(), @scopeName)
                RETURNING id)
            INSERT INTO heimdall.pending_registration
                (public_id, scope_id, name, email, password_hash, salt, token_hash, expires_at)
            SELECT @publicId, s.id, 'Registrant', 'registrant@test.local', @bytes, @bytes, @tokenHash,
                   now() + interval '1 hour'
            FROM s
            """,
            connection);
        command.Parameters.AddWithValue("scopeName", $"schema-{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("publicId", publicId);
        command.Parameters.AddWithValue("bytes", new byte[] { 0 });
        command.Parameters.AddWithValue("tokenHash", Guid.NewGuid().ToString("N"));
        await command.ExecuteNonQueryAsync();
    }

    [FunctionalFact]
    public async Task GivenRegistrationsStoredBeforeTheMigration_WhenMigrated_ThenEachHasItsOwnPublicId()
    {
        // Given a fresh database at the migration before this one, holding two pending registrations
        var database = $"heimdall_migration_{Guid.NewGuid():N}";
        await ExecuteAsync(fixture.ConnectionString, $"CREATE DATABASE {database}");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = database
        }.ConnectionString;

        try
        {
            await using (var context = ContextFor(connectionString))
            {
                var migrations = context.Database.GetMigrations().ToList();
                var index = migrations.FindIndex(name => name.EndsWith(MigrationSuffix, StringComparison.Ordinal));
                Assert.True(index > 0, "AddPendingRegistrationPublicId is not among the migrations.");

                await context.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
            }

            await ExecuteAsync(
                connectionString,
                """
                WITH s AS (
                    INSERT INTO heimdall.scope (public_id, name) VALUES (gen_random_uuid(), 'migration-scope')
                    RETURNING id)
                INSERT INTO heimdall.pending_registration
                    (scope_id, name, email, password_hash, salt, token_hash, expires_at)
                SELECT s.id, 'Registrant ' || n, 'registrant' || n || '@test.local', '\x00'::bytea,
                       '\x00'::bytea, md5(n::text), now() + interval '1 hour'
                FROM s, generate_series(1, 2) AS n;
                """);

            // When
            await using (var context = ContextFor(connectionString))
            {
                await context.Database.MigrateAsync();
            }

            // Then — both rows backfilled, with distinct, non-empty ids
            var ids = await ReadPublicIdsAsync(connectionString);
            Assert.Equal(2, ids.Count);
            Assert.DoesNotContain(Guid.Empty, ids);
            Assert.Equal(2, ids.Distinct().Count());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(fixture.ConnectionString, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }
    }

    [FunctionalFact]
    public async Task GivenTwoRegistrationsWithOnePublicId_WhenInserted_ThenTheDatabaseRefusesTheSecond()
    {
        // Given one row already holding the id
        var publicId = Guid.NewGuid();
        await InsertPendingAsync(fixture.ConnectionString, publicId);

        // When
        var second = await Record.ExceptionAsync(() => InsertPendingAsync(fixture.ConnectionString, publicId));

        // Then
        var failure = Assert.IsType<PostgresException>(second);
        Assert.Equal("23505", failure.SqlState);
        Assert.Equal("ix_pending_registration_public_id", failure.ConstraintName);
    }
}
