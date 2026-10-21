using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Data.Seeding;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     The two-role database setup (NFR-29, Threat Model TH-18): the runtime login the API connects
///     as owns nothing, so it cannot alter, disable or remove the audit triggers or their functions,
///     nor delete or truncate the trail, while everything the API does still works.
/// </summary>
/// <remarks>
///     <para>
///         The triggers on <c>audit_log</c> bind every role but the table's owner and a superuser. A
///         deployment whose API login owns the schema — what the deployment guide used to create —
///         therefore gets nothing from them against that login's credentials. These tests apply the
///         committed scripts, <c>scripts/sql/new-deployment.sql</c> and
///         <c>scripts/sql/separate-owner.sql</c>, with psql inside the test container, exactly as an
///         operator runs them, and then connect as the runtime login.
///     </para>
///     <para>
///         The new deployment is the fixture's own database, which the whole functional suite uses
///         as the runtime login. The switch-over gets a database of its own per test, migrated the
///         old way: created and migrated by the login that will become the runtime one.
///     </para>
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class DatabaseRoleTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string LegacyPassword = "functional-tests-legacy-password";

    /// <summary>
    ///     What a holder of the API's credentials would run to rewrite the trail or the schema under
    ///     it. Each must be refused for want of privilege, not stopped by a trigger: a trigger is
    ///     only worth its guarantee while the login cannot remove it.
    /// </summary>
    private static readonly string[] Refused =
    [
        "ALTER TABLE heimdall.audit_log DISABLE TRIGGER USER",
        "ALTER TABLE heimdall.audit_log DISABLE TRIGGER audit_log_no_delete",
        "ALTER TABLE heimdall.audit_log DISABLE TRIGGER ALL",
        "DROP TRIGGER audit_log_no_delete ON heimdall.audit_log",
        "DROP TRIGGER audit_log_no_truncate ON heimdall.audit_log",
        "DROP TRIGGER audit_log_attribution_clear_only ON heimdall.audit_log",
        """
        CREATE OR REPLACE FUNCTION heimdall.audit_log_attribution_clear_only() RETURNS trigger
        AS $$ BEGIN RETURN NEW; END; $$ LANGUAGE plpgsql
        """,
        """
        CREATE OR REPLACE FUNCTION heimdall.audit_log_is_append_only() RETURNS trigger
        AS $$ BEGIN RETURN NULL; END; $$ LANGUAGE plpgsql
        """,
        "DROP FUNCTION heimdall.audit_log_is_append_only() CASCADE",
        "ALTER FUNCTION heimdall.audit_log_attribution_clear_only() RENAME TO planted",
        """
        CREATE TRIGGER planted BEFORE INSERT ON heimdall.audit_log
        FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_immutable()
        """,
        "TRUNCATE heimdall.audit_log",
        "DELETE FROM heimdall.audit_log",
        "DROP TABLE heimdall.audit_log",
        "ALTER TABLE heimdall.erased_subject DISABLE TRIGGER USER",
        "ALTER TABLE heimdall.person DISABLE TRIGGER USER",
        "SET session_replication_role = replica",
        "CREATE TABLE heimdall.planted (id integer)",
        "CREATE TABLE public.planted (id integer)",
        "CREATE SCHEMA planted",
        """DELETE FROM public."__EFMigrationsHistory" """,
        """INSERT INTO public."__EFMigrationsHistory" VALUES ('99999999999999_Planted', '10.0.0')"""
    ];

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<T>> ReadAsync<T>(
        string connectionString, string sql, Func<NpgsqlDataReader, T> row, params (string Name, object Value)[] parameters)
    {
        var rows = new List<T>();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add(row(reader));
        }

        return rows;
    }

    /// <summary>
    ///     Runs one statement in a transaction that is always rolled back, so a statement that
    ///     wrongly succeeds still changes nothing, and returns the error it raised, if any.
    /// </summary>
    private static async Task<PostgresException?> ErrorOfAsync(string connectionString, string statement)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(statement, connection, transaction);

        try
        {
            await command.ExecuteNonQueryAsync();

            return null;
        }
        catch (PostgresException exception)
        {
            return exception;
        }
    }

    /// <summary>
    ///     Every <see cref="Refused" /> statement, and an <c>ALTER TABLE</c> on every table, run as
    ///     the runtime login. Returns the ones not refused for want of privilege, with what happened
    ///     instead, so a failure names each hole rather than the first.
    /// </summary>
    private static async Task<List<string>> NotRefusedAsync(string superuser, string runtime)
    {
        var tables = await ReadAsync(superuser,
            "SELECT format('%I.%I', schemaname, tablename) FROM pg_tables " +
            "WHERE schemaname IN ('heimdall', 'public') ORDER BY 1",
            reader => reader.GetString(0));

        Assert.Contains("heimdall.audit_log", tables);

        var statements = Refused.Concat(tables.Select(table => $"ALTER TABLE {table} ADD COLUMN planted integer"));
        var holes = new List<string>();

        foreach (var statement in statements)
        {
            var error = await ErrorOfAsync(runtime, statement);

            if (error?.SqlState != PostgresErrorCodes.InsufficientPrivilege)
            {
                holes.Add($"{statement.ReplaceLineEndings(" ")} => {error?.MessageText ?? "succeeded"}");
            }
        }

        return holes;
    }

    /// <summary>
    ///     The table privileges <paramref name="role" /> holds in the database, from the catalog, as
    ///     "schema.table: PRIVILEGE, …", PUBLIC's and any membership's included.
    /// </summary>
    private static async Task<Dictionary<string, string>> TablePrivilegesAsync(string superuser, string role)
    {
        var rows = await ReadAsync(superuser,
            """
            SELECT format('%I.%I', n.nspname, c.relname),
                   coalesce(string_agg(p.privilege, ', ' ORDER BY p.ordinal)
                            FILTER (WHERE has_table_privilege(@role, c.oid, p.privilege)), '')
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            CROSS JOIN unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES',
                                    'TRIGGER', 'MAINTAIN']) WITH ORDINALITY AS p(privilege, ordinal)
            WHERE n.nspname IN ('heimdall', 'public') AND c.relkind IN ('r', 'p')
            GROUP BY 1
            """,
            reader => (reader.GetString(0), reader.GetString(1)),
            ("role", role));

        return rows.ToDictionary(row => row.Item1, row => row.Item2);
    }

    /// <summary>
    ///     Asserts the runtime login holds exactly the privileges the API needs: rows on every table,
    ///     no <c>DELETE</c> on the trail, nothing but reads on the migration history, sequences to
    ///     draw from, the schemas to look up, and no ownership, creation or membership anywhere.
    /// </summary>
    private static async Task AssertRuntimePrivilegesAsync(string superuser, string runtime, string owner)
    {
        var tables = await TablePrivilegesAsync(superuser, runtime);

        Assert.True(tables.Count > 10, $"Only {tables.Count} tables found.");

        foreach (var (table, privileges) in tables)
        {
            var expected = table switch
            {
                "heimdall.audit_log" => "SELECT, INSERT, UPDATE",
                _ when table.StartsWith("heimdall.") => "SELECT, INSERT, UPDATE, DELETE",
                _ => "SELECT"
            };

            Assert.True(expected == privileges, $"{table}: expected [{expected}], found [{privileges}]");
        }

        var facts = (await ReadAsync(superuser,
            """
            SELECT
                (SELECT count(*) FROM pg_shdepend
                 WHERE deptype = 'o' AND refobjid = r.oid
                   AND dbid = (SELECT oid FROM pg_database WHERE datname = current_database())),
                (SELECT datdba = r.oid FROM pg_database WHERE datname = current_database()),
                has_database_privilege(r.oid, current_database(), 'CREATE'),
                has_schema_privilege(r.oid, 'heimdall', 'CREATE'),
                has_schema_privilege(r.oid, 'public', 'CREATE'),
                has_schema_privilege(r.oid, 'heimdall', 'USAGE'),
                pg_has_role(r.oid, @owner, 'MEMBER'),
                r.rolsuper OR r.rolcreaterole,
                (SELECT bool_and(has_sequence_privilege(r.oid, c.oid, 'USAGE')
                                 AND NOT has_sequence_privilege(r.oid, c.oid, 'UPDATE'))
                 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE n.nspname = 'heimdall' AND c.relkind = 'S')
            FROM pg_roles r WHERE r.rolname = @runtime
            """,
            reader => new
            {
                OwnedObjects = reader.GetInt64(0),
                OwnsDatabase = reader.GetBoolean(1),
                CreateInDatabase = reader.GetBoolean(2),
                CreateInHeimdall = reader.GetBoolean(3),
                CreateInPublic = reader.GetBoolean(4),
                UsesHeimdall = reader.GetBoolean(5),
                MemberOfOwner = reader.GetBoolean(6),
                Privileged = reader.GetBoolean(7),
                DrawsFromSequences = reader.GetBoolean(8)
            },
            ("runtime", runtime), ("owner", owner))).Single();

        Assert.Equal(0, facts.OwnedObjects);
        Assert.False(facts.OwnsDatabase);
        Assert.False(facts.CreateInDatabase);
        Assert.False(facts.CreateInHeimdall);
        Assert.False(facts.CreateInPublic);
        Assert.True(facts.UsesHeimdall);
        Assert.False(facts.MemberOfOwner);
        Assert.False(facts.Privileged);
        Assert.True(facts.DrawsFromSequences);
    }

    private static readonly Dictionary<string, string> NoVariables = new();

    private static Dictionary<string, string> Variables(string owner, string runtime, string database) => new()
    {
        ["owner"] = owner, ["runtime"] = runtime, ["database"] = database
    };

    /// <summary>
    ///     A database set up as the deployment guide used to: one login creates and owns it and
    ///     applies the migrations, then the API runs as that same login.
    /// </summary>
    private async Task<LegacyDeployment> CreateLegacyDeploymentAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var legacy = new LegacyDeployment(db, $"legacy_owner_{suffix}", $"legacy_svc_{suffix}", $"legacy_{suffix}");
        var admin = db.ConnectionStringFor("postgres", "postgres", "postgres");

        await ExecuteAsync(admin, $"CREATE ROLE {legacy.Runtime} LOGIN PASSWORD '{LegacyPassword}'");
        await ExecuteAsync(admin, $"CREATE DATABASE {legacy.Database} OWNER {legacy.Runtime}");

        await using var context = PostgresFixture.CreateContext(legacy.RuntimeConnectionString);
        await context.Database.MigrateAsync();

        return legacy;
    }

    private sealed record LegacyDeployment(PostgresFixture Db, string Owner, string Runtime, string Database)
        : IAsyncDisposable
    {
        public string Superuser => Db.ConnectionStringFor("postgres", "postgres", Database);
        public string OwnerConnectionString => Db.ConnectionStringFor(Owner, LegacyPassword, Database);
        public string RuntimeConnectionString => Db.ConnectionStringFor(Runtime, LegacyPassword, Database);

        public async ValueTask DisposeAsync()
        {
            var admin = Db.ConnectionStringFor("postgres", "postgres", "postgres");

            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS {Database} WITH (FORCE)");
            await ExecuteAsync(admin, $"DROP ROLE IF EXISTS {Owner}");
            await ExecuteAsync(admin, $"DROP ROLE IF EXISTS {Runtime}");
        }
    }

    [FunctionalFact]
    public async Task GivenANewDeployment_WhenTheRuntimeLoginTriesToAlterTheTrailOrTheSchema_ThenEveryStatementIsRefused()
    {
        var holes = await NotRefusedAsync(db.ConnectionString, db.RuntimeConnectionString);

        Assert.True(holes.Count == 0, string.Join(Environment.NewLine, holes));
    }

    [FunctionalFact]
    public async Task GivenANewDeployment_WhenTheCheckRunsAsTheRuntimeLogin_ThenItPasses()
    {
        var result = await db.RunScriptAsync(
            "check-runtime-login.sql", NoVariables, PostgresFixture.RuntimeRole, PostgresFixture.Database);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Contains($"{PostgresFixture.RuntimeRole} was refused all", result.Stderr);
    }

    [FunctionalFact]
    public async Task GivenANewDeployment_WhenTheCheckRunsAsTheOwner_ThenItFailsListingTheHolesAndChangesNothing()
    {
        // The wrong session must be told apart from a restricted one, and the check must leave the
        // triggers it found it could remove exactly as they were.
        var result = await db.RunScriptAsync(
            "check-runtime-login.sql", NoVariables, PostgresFixture.OwnerRole, PostgresFixture.Database);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ALTER TABLE heimdall.audit_log DISABLE TRIGGER USER: not refused", result.Stderr);

        var triggers = await ReadAsync(db.ConnectionString,
            "SELECT tgname || '=' || tgenabled::text FROM pg_trigger " +
            "WHERE tgrelid = 'heimdall.audit_log'::regclass AND NOT tgisinternal ORDER BY 1",
            reader => reader.GetString(0));
        Assert.Equal(
            ["audit_log_attribution_clear_only=O", "audit_log_no_delete=O", "audit_log_no_truncate=O"],
            triggers);
    }

    [FunctionalFact]
    public async Task GivenANewDeployment_WhenTheRuntimeLoginsPrivilegesAreRead_ThenItHoldsRowPrivilegesAndNothingMore()
    {
        await AssertRuntimePrivilegesAsync(db.ConnectionString, PostgresFixture.RuntimeRole, PostgresFixture.OwnerRole);
    }

    [FunctionalFact]
    public async Task GivenALaterMigration_WhenTheOwnerCreatesATable_ThenTheRuntimeLoginMayWriteItsRowsAndNothingMore()
    {
        // Default privileges, recorded against the owner, are what keep a later migration from having
        // to grant its own tables. Created and checked in one transaction, rolled back.
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var create = new NpgsqlCommand(
                         "CREATE TABLE heimdall.later_table (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY)",
                         connection, transaction))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var read = new NpgsqlCommand(
            """
            SELECT has_table_privilege(@runtime, 'heimdall.later_table', 'SELECT, INSERT, UPDATE, DELETE'),
                   has_table_privilege(@runtime, 'heimdall.later_table', 'TRUNCATE, REFERENCES, TRIGGER'),
                   has_sequence_privilege(@runtime, 'heimdall.later_table_id_seq', 'USAGE')
            """,
            connection, transaction);
        read.Parameters.AddWithValue("runtime", PostgresFixture.RuntimeRole);

        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
    }

    [FunctionalFact]
    public async Task GivenTheApiRunningAsTheRuntimeLogin_WhenAnAdministratorCreatesAnApplication_ThenTheWriteAndItsAuditEntrySucceed()
    {
        // Given — the API under test connects as the login that owns nothing
        var configured = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("HEIMDALL_DATA_CONNECTIONSTRING"));
        Assert.Equal(PostgresFixture.RuntimeRole, configured.Username);

        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        var admin = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Root", Email = $"root-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.SystemAdmin, EmailVerified = true
        };
        var owner = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Owner", Email = $"owner-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.ScopeAdmin, EmailVerified = true
        };
        context.Scopes.Add(scope);
        context.Persons.AddRange(admin, owner);
        await context.SaveChangesAsync();
        context.ScopeOwners.Add(new ScopeOwner { ScopeId = scope.Id, PersonId = owner.Id });
        await context.SaveChangesAsync();

        Authorize(TestTokens.For(admin.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/applications",
            new CreateApplicationCommand { Name = $"app-{Guid.NewGuid():N}", OwnerId = owner.PublicId });

        // Then — the application and its audit entry were written, by the runtime login
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var entry = await context.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == nameof(CreateApplicationCommand) && a.TargetId == response.Body!.Data!.Id);
        Assert.Equal(admin.PublicId, entry.ActorPersonId);

        var connected = await ReadAsync(db.ConnectionString,
            "SELECT count(*) FROM pg_stat_activity WHERE usename = @runtime AND datname = @database",
            reader => reader.GetInt64(0),
            ("runtime", PostgresFixture.RuntimeRole), ("database", PostgresFixture.Database));
        Assert.True(connected.Single() > 0);
    }

    [FunctionalFact]
    public async Task GivenTheRuntimeLogin_WhenItClearsAnAttributionPastTheFloor_ThenTheTriggerStillDecides()
    {
        // The retention pass writes the rows it clears whole, so audit_log keeps UPDATE; which updates
        // are accepted stays the trigger's decision.
        var due = Guid.NewGuid();
        var recent = Guid.NewGuid();

        await ExecuteAsync(db.RuntimeConnectionString,
            $"""
             INSERT INTO heimdall.audit_log (public_id, action, succeeded, actor_person_id, actor_role, created_at)
             VALUES ('{due}', 'DatabaseRoleTestCommand', true, gen_random_uuid(), 1, now() - interval '400 days'),
                    ('{recent}', 'DatabaseRoleTestCommand', true, gen_random_uuid(), 1, now())
             """);

        var cleared = await ErrorOfAsync(db.RuntimeConnectionString,
            $"UPDATE heimdall.audit_log SET actor_person_id = NULL, actor_role = NULL WHERE public_id = '{due}'");
        var refused = await ErrorOfAsync(db.RuntimeConnectionString,
            $"UPDATE heimdall.audit_log SET actor_person_id = NULL, actor_role = NULL WHERE public_id = '{recent}'");

        Assert.Null(cleared);
        Assert.Equal(PostgresErrorCodes.RestrictViolation, refused?.SqlState);
    }

    [FunctionalFact]
    public async Task GivenADatabaseOwnedByTheRuntimeLogin_WhenSeparateOwnerRunsTwice_ThenTheLoginIsRefusedAsInANewDeployment()
    {
        // Given — a database migrated the old way, where the API's login can switch the triggers off
        await using var legacy = await CreateLegacyDeploymentAsync();

        Assert.Null(await ErrorOfAsync(legacy.RuntimeConnectionString,
            "ALTER TABLE heimdall.audit_log DISABLE TRIGGER USER"));

        // When — the switch-over, twice: the runbook's retry must be safe
        var variables = Variables(legacy.Owner, legacy.Runtime, legacy.Database);
        var first = await db.RunScriptAsync("separate-owner.sql", variables);
        var second = await db.RunScriptAsync("separate-owner.sql", variables);

        // Then
        Assert.True(first.ExitCode == 0, first.Stderr);
        Assert.True(second.ExitCode == 0, second.Stderr);

        var holes = await NotRefusedAsync(legacy.Superuser, legacy.RuntimeConnectionString);
        Assert.True(holes.Count == 0, string.Join(Environment.NewLine, holes));

        await AssertRuntimePrivilegesAsync(legacy.Superuser, legacy.Runtime, legacy.Owner);

        var check = await db.RunScriptAsync("check-runtime-login.sql", NoVariables, legacy.Runtime, legacy.Database);
        Assert.True(check.ExitCode == 0, check.Stderr);
    }

    [FunctionalFact]
    public async Task GivenADatabaseOwnedByTheRuntimeLogin_WhenSeparated_ThenTheApplicationRunsAndOnlyTheOwnerMigrates()
    {
        // Given
        await using var legacy = await CreateLegacyDeploymentAsync();

        // When
        var result = await db.RunScriptAsync("separate-owner.sql", Variables(legacy.Owner, legacy.Runtime, legacy.Database));
        Assert.True(result.ExitCode == 0, result.Stderr);
        await db.SetPasswordAsync(legacy.Owner, LegacyPassword);

        // Then — the start-up seeder, which reads the migration history and writes rows, runs as the
        // runtime login, and so does an audit entry and its clearing
        await using (var runtime = PostgresFixture.CreateContext(legacy.RuntimeConnectionString))
        {
            await new DatabaseSeeder(runtime, MasterUserOptions.FromEnvironment(), NullLogger<DatabaseSeeder>.Instance)
                .SeedAsync();
        }

        var entry = Guid.NewGuid();
        await ExecuteAsync(legacy.RuntimeConnectionString,
            $"""
             INSERT INTO heimdall.audit_log (public_id, action, succeeded, actor_person_id, actor_role, created_at)
             VALUES ('{entry}', 'DatabaseRoleTestCommand', true, gen_random_uuid(), 1, now() - interval '400 days')
             """);
        await ExecuteAsync(legacy.RuntimeConnectionString,
            $"UPDATE heimdall.audit_log SET actor_person_id = NULL, actor_role = NULL WHERE public_id = '{entry}'");

        // The owner migrates. The runtime login no longer can, even with nothing pending, which is why
        // the runbook has the owner's connection string in place before the next deploy.
        await using (var owner = PostgresFixture.CreateContext(legacy.OwnerConnectionString))
        {
            await owner.Database.MigrateAsync();
        }

        await using (var runtime = PostgresFixture.CreateContext(legacy.RuntimeConnectionString))
        {
            var failure = await Record.ExceptionAsync(() => runtime.Database.MigrateAsync());

            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(failure).SqlState);
        }
    }

    [FunctionalFact]
    public async Task GivenARuntimeLoginThatOwnsAnotherDatabase_WhenSeparateOwnerRuns_ThenItRefusesAndChangesNothing()
    {
        // REASSIGN OWNED moves every database the login owns, not only this one, so the script stops
        await using var legacy = await CreateLegacyDeploymentAsync();
        var other = $"{legacy.Database}_other";
        var admin = db.ConnectionStringFor("postgres", "postgres", "postgres");
        await ExecuteAsync(admin, $"CREATE DATABASE {other} OWNER {legacy.Runtime}");

        try
        {
            var result = await db.RunScriptAsync("separate-owner.sql", Variables(legacy.Owner, legacy.Runtime, legacy.Database));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(other, result.Stderr);

            var owners = await ReadAsync(admin,
                "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname IN (@a, @b)",
                reader => reader.GetString(0),
                ("a", legacy.Database), ("b", other));
            Assert.All(owners, owner => Assert.Equal(legacy.Runtime, owner));
            Assert.Null(await ErrorOfAsync(legacy.RuntimeConnectionString,
                "ALTER TABLE heimdall.audit_log DISABLE TRIGGER USER"));
        }
        finally
        {
            await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS {other} WITH (FORCE)");
        }
    }

    [FunctionalFact]
    public async Task GivenADatabaseOwnedByTheRuntimeLogin_WhenNewDeploymentRuns_ThenItRefusesAndPointsToTheSwitchOver()
    {
        await using var legacy = await CreateLegacyDeploymentAsync();

        var result = await db.RunScriptAsync("new-deployment.sql", Variables(legacy.Owner, legacy.Runtime, legacy.Database));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("separate-owner.sql", result.Stderr);
    }

    [FunctionalTheory]
    [InlineData("new-deployment.sql")]
    [InlineData("separate-owner.sql")]
    public async Task GivenAMissingVariable_WhenAScriptRuns_ThenItRefusesNamingTheVariable(string script)
    {
        var result = await db.RunScriptAsync(script, new Dictionary<string, string>
        {
            ["owner"] = "missing_variable_owner", ["database"] = "missing_variable_database"
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("runtime", result.Stderr);
    }
}
