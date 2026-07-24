# Data Infrastructure — Design

Date: 2026-07-23

## 1. Purpose

Finish the data layer so the API can run against a real PostgreSQL database: name the schema the way
Postgres expects, generate and apply EF Core migrations from a repeatable script, guarantee the
reference data the application depends on, and stop leaking sensitive values in production logs.

`AppDbContext`, the nine `EntityMaps`, and the DI registration
(`AddPostgreSqlProvider()` + `AddDataConfigFromEnvironment<AppDbContext>("IDENTITY_MANAGER_DATA")`)
already exist. This design covers what is still missing.

## 2. Current gaps

| # | Gap | Consequence |
| --- | --- | --- |
| 1 | No table/column naming configuration | EF would emit quoted PascalCase plural names, clashing with the `identity_manager` schema and the ERD names in the requirements |
| 2 | No `Migrations` folder, no design-time factory | `dotnet ef` cannot construct `AppDbContext` (its constructor takes an `ILoggerFactory`), so no migration can be generated |
| 3 | No seeding | `CreateScopeCommandHandler` returns `ScopeAdminRoleNotConfigured` on a fresh database; nobody can sign in as a System Admin |
| 4 | `Environments/` and `Settings/` are not copied to the build output | `ConfigurationLoader` finds no `.env` and no `appsettings.json` at runtime, so the connection string is never loaded |
| 5 | `PostgresFixture` has an unresolved TODO | Functional tests run against an empty container |
| 6 | `EnableSensitiveDataLogging()`/`EnableDetailedErrors()` are unconditional | Parameter values (including password hashes and e-mail addresses) reach production logs |

## 3. Naming convention — snake_case, singular

Tables and columns use snake_case; table names are singular. This matches the `identity_manager`
schema name and the entity names used throughout the requirements (`SCOPE_OWNER`, `GOOGLE_USER`),
and avoids double-quoting every identifier in hand-written SQL.

- Add `EFCore.NamingConventions` `10.0.1` to `ArturRios.IdentityManager.Data` and call
  `.UseSnakeCaseNamingConvention()` in `AppDbContext.OnConfiguring`. This rewrites columns, keys,
  indexes, and foreign-key constraint names.
- Table names are otherwise derived from the `DbSet` property names, which are plural. Each
  `*DbMap.Configure` therefore adds one explicit `ToTable(...)` call:

  | Entity | Table |
  | --- | --- |
  | `Person` | `person` |
  | `Scope` | `scope` |
  | `Role` | `role` |
  | `Application` | `application` |
  | `GoogleUser` | `google_user` |
  | `ScopeOwner` | `scope_owner` |
  | `ScopeUser` | `scope_user` |
  | `PasswordResetToken` | `password_reset_token` |
  | `EmailVerificationToken` | `email_verification_token` |

The names passed to `ToTable` are already snake_case, so the convention leaves them untouched.

## 4. Design-time factory and migrations

`dotnet ef` must be able to build an `AppDbContext` without booting the Web API.

- Add `Microsoft.EntityFrameworkCore.Design` (`PrivateAssets="all"`) to the Data project, plus
  `<GenerateRuntimeConfigurationFiles>true</GenerateRuntimeConfigurationFiles>` so the class library
  can serve as its own `--startup-project`.
- Add `Configuration/DesignTimeDbContextFactory.cs` implementing
  `IDesignTimeDbContextFactory<AppDbContext>`. It reads `IDENTITY_MANAGER_DATA_CONNECTIONSTRING`
  from the process environment and throws a message naming the variable and pointing at
  `scripts/migrations.py` when it is absent or blank. It supplies `NullLoggerFactory.Instance` and
  `DbContextDiagnosticsOptions.Disabled` (§7) to the context.
- Migrations live in `src/Infrastructure/ArturRios.IdentityManager.Data/Migrations/`. The first one
  is named `InitialCreate` and covers all nine entities.

If `dotnet ef` turns out to reject the class library as a startup project, the fallback is to point
`--startup-project` at the Web API; the factory is discovered in either assembly, so no code
changes. This is a tooling detail to confirm during implementation, not a design fork.

## 5. Seeding

A single idempotent `Seeding/DatabaseSeeder.cs` in the Data project, resolved from a scope and run
on every startup. It takes `AppDbContext`, an `ILogger<DatabaseSeeder>`, and a `MasterUserOptions`
record carrying the three `IDENTITY_MANAGER_MASTER_USER_*` values.

`SeedAsync` performs three steps in order:

1. **Pending-migration guard.** `Database.GetPendingMigrationsAsync()`. If the list is non-empty, log
   the pending migration names at fatal level and throw, so the API refuses to start against a stale
   schema. Migrations are applied deliberately through `scripts/migrations.py`, never implicitly by
   the application.
2. **Roles.** `role.id` equals the `Roles` enum value: `SystemAdmin = 1`, `ScopeAdmin = 2`,
   `User = 3`. For every member of the enum, insert a `Role` with `Id = (long)member` when that ID is
   absent, and correct `Name`/`Description` on an existing row if they have drifted. `Name` is the
   enum member name; `Description` comes from its `[Description]` attribute.

   To make this deterministic, `RoleDbMap` configures the key as `ValueGeneratedNever()`. Roles are
   fixed reference data (FR-RO-01 admits exactly three), so the column is a plain `bigint` primary
   key rather than an identity column — IDs are assigned by the enum, not the database, and there is
   no sequence left trailing behind the explicitly inserted rows. `CreateScopeCommandHandler`
   continues to look roles up by `Name`; the seeder keeps both keys consistent.
3. **System administrator.** If no non-deleted `Person` holds the `SystemAdmin` role, create one from
   `MasterUserOptions` with `Hash.EncodeWithRandomSalt` (Argon2id, from `ArturRios.Util`) and
   `EmailVerified = true`. When no admin exists *and* the master-user variables are missing or blank,
   throw — an identity system with no way in is a misconfiguration, not a degraded mode.

The Data project gains a package reference to `ArturRios.Util` for `Hash`.

Wiring: `Startup.AddDependencies` registers `DatabaseSeeder` and `MasterUserOptions` (read via the
`EnvironmentProvider` that `LoadConfiguration` already registers). `Startup.StartServices` — a
virtual hook already invoked at the end of `Build()` — creates a scope, resolves the seeder, and
runs it synchronously before the host starts serving.

## 6. Configuration plumbing

`ConfigurationLoader` resolves `Environments/.env.<environment>` (falling back to
`Environments/.env.local`) relative to the **application base path**, i.e. `bin/…/net10.0/`. Neither
folder is currently copied there.

Add to `ArturRios.IdentityManager.WebApi.csproj`:

```xml
<ItemGroup>
  <Content Include="Environments\.env*" CopyToOutputDirectory="PreserveNewest" />
  <Content Include="Settings\appsettings*.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

The Web SDK's default globs only pick up `appsettings*.json` at the project root, so the `Settings`
entry is required and does not collide. `<None Remove>` is applied first if the SDK reports a
duplicate include.

**Local template.** `.env.local` is gitignored, so the tracked `Environments/.env` remains the
template of record. Two things make that usable: `scripts/migrations.py` offers to create
`.env.local` from `.env` when no environment file exists, and the README documents the copy step.

## 7. Diagnostics gating

`EnableSensitiveDataLogging()` writes parameter values — password hashes, salts, e-mail addresses —
into logs. `EnableDetailedErrors()` puts column values into exception messages. Both are useful
locally and unacceptable in production.

Add `Configuration/DbContextDiagnosticsOptions.cs` to the Data project:

```csharp
public sealed class DbContextDiagnosticsOptions
{
    public static readonly DbContextDiagnosticsOptions Disabled = new();

    public bool SensitiveDataLogging { get; init; }
    public bool DetailedErrors { get; init; }
}
```

Both default to `false`, so any environment we fail to classify is treated as production. The
`AppDbContext` constructor takes it as a third parameter and `OnConfiguring` applies each flag only
when set. `Startup.AddDependencies` registers a singleton built from
`!Builder.Environment.IsProduction()`; `DesignTimeDbContextFactory` passes `Disabled`.

The existing constructor already takes `ILoggerFactory`, which confirms
`AddDataConfigFromEnvironment` activates the context through standard DI, so a third registered
service resolves without further changes.

## 8. `scripts/migrations.py`

A command-line menu at the repository root, Python 3, standard library only.

**Startup:** resolve the repository root from `__file__`; list the files matching
`src/Presentation/ArturRios.IdentityManager.WebApi/Environments/.env*`; prompt for one. Parse it into
a dict — tolerating a UTF-8 BOM, `KEY="quoted"` values, `#` comments and blank lines — and merge it
into a copy of `os.environ` used for every subprocess. If no environment file exists, offer to copy
the `.env` template to `.env.local` and exit so the user can fill it in.

**Menu:**

| Option | Command |
| --- | --- |
| List migrations | `dotnet ef migrations list` |
| Create a migration | prompt for a name, validate it is non-empty and alphanumeric, then `dotnet ef migrations add <Name> --output-dir Migrations` |
| Apply migrations | print the target host and database (never the password), confirm, then `dotnet ef database update` |

Every command passes `--project` and `--startup-project` pointing at
`src/Infrastructure/ArturRios.IdentityManager.Data/ArturRios.IdentityManager.Data.csproj`. The menu
loops until the user exits, and a non-zero exit from `dotnet ef` is reported without killing the
session.

**Tooling:** a `.config/dotnet-tools.json` manifest pins `dotnet-ef`. When `dotnet ef` is not on the
path, the script prints `dotnet tool restore` rather than failing opaquely.

## 9. Tests

`PostgresFixture.InitializeAsync` replaces its TODO: after the container starts and the connection
string variables are set, construct an `AppDbContext` against the container and call `MigrateAsync()`
so functional tests exercise the real migrated schema, as required by Testing Specification §7.2.
The fixture also sets the `IDENTITY_MANAGER_MASTER_USER_*` variables, since the seeder runs inside
the functional host and refuses to start without them.

Verification for this change is the existing suite plus a manual migration round-trip:
`dotnet build`, `dotnet test`, then list/create/apply through `scripts/migrations.py` against a local
database.

## 10. Out of scope

- Authentication and token issuance; the master user is seeded, not signed in.
- Any use case beyond UC-01/UC-02, which are already implemented.
- Rolling back or squashing migrations — the script exposes list, create and apply only.
