# Data Infrastructure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Identity Manager API run against a real PostgreSQL database — snake_case schema, EF Core migrations driven by a Python menu, guaranteed reference data, and no sensitive-value logging in production.

**Architecture:** `ArturRios.IdentityManager.Data` keeps the `AppDbContext` and gains three things: a design-time factory so `dotnet ef` can build the context without booting the API, a `Migrations` folder, and an idempotent `DatabaseSeeder` the Web API runs at startup. Migrations are never applied by the application — `scripts/migrations.py` applies them, and the seeder refuses to start against a schema with pending migrations.

**Tech Stack:** .NET 10, EF Core 10.0.10, Npgsql 10.0.3, `EFCore.NamingConventions` 10.0.1, `ArturRios.Data.Relational.Core` 3.0.0, `ArturRios.Util` (Argon2id hashing), xUnit + Testcontainers, Python 3 (standard library only).

## Global Constraints

- Target framework `net10.0`; `Nullable` and `ImplicitUsings` enabled on every project.
- Database schema is `identity_manager`. Tables are **snake_case and singular**; columns, keys and indexes are snake_case.
- `role.id` equals the `Roles` enum value: `SystemAdmin = 1`, `ScopeAdmin = 2`, `User = 3`.
- EF diagnostics (`EnableSensitiveDataLogging`, `EnableDetailedErrors`) default to **off**; they are enabled only when the host environment is not Production.
- Migrations live in `src/Infrastructure/ArturRios.IdentityManager.Data/Migrations/` and are applied only through `scripts/migrations.py`.
- The Python script uses the standard library only — no pip installs.
- Test names follow Given/When/Then per `docs/Testing Specification Document.md`: `GivenSomeCondition_WhenSomeAction_ThenSomeOutput` in C#, `test_given_..._when_..._then_...` in Python.
- Package versions to use exactly: `EFCore.NamingConventions` `10.0.1`, `Microsoft.EntityFrameworkCore.Design` `10.0.10`, `dotnet-ef` `10.0.10`, `ArturRios.Util` `1.4.0`.

## Baseline (verified 2026-07-23)

- `dotnet build src/ArturRios.IdentityManager.sln` — succeeds, 0 warnings.
- `dotnet test src/ArturRios.IdentityManager.sln` — **1 test, failing.** `HealthCheckTests` throws `DataAccessException: Environment variable 'IDENTITY_MANAGER_DATA_DATABASETYPE' is unset` because the class never joins `FunctionalCollection`, so no container starts. Task 4 fixes this.
- `CreateScopeCommandHandlerTests`, `GetScopeByIdQueryHandlerTests` and `ListScopesQueryHandlerTests` are empty scaffolding classes with no test methods. Leave them alone — filling them in is not part of this plan.
- **Docker was not running.** Tasks 4 and 6 need it for Testcontainers. Start Docker Desktop before those tasks and confirm with `docker version`.

## File Structure

| File | Responsibility |
| --- | --- |
| `src/Presentation/.../ArturRios.IdentityManager.WebApi.csproj` | Copies `Environments/` and `Settings/` to the build output |
| `src/Infrastructure/.../Configuration/DbContextDiagnosticsOptions.cs` | **New.** Carries the two diagnostics flags, defaulting to off |
| `src/Infrastructure/.../Configuration/AppDbContext.cs` | Applies snake_case naming and the diagnostics flags |
| `src/Infrastructure/.../Configuration/DesignTimeDbContextFactory.cs` | **New.** Builds the context for `dotnet ef` from the environment |
| `src/Infrastructure/.../EntityMaps/*DbMap.cs` | Singular table names; `RoleDbMap` also pins the key to explicit values |
| `src/Infrastructure/.../Migrations/` | **New.** Generated EF migrations |
| `src/Infrastructure/.../Seeding/MasterUserOptions.cs` | **New.** Master-user credentials read from the environment |
| `src/Infrastructure/.../Seeding/DatabaseSeeder.cs` | **New.** Migration guard, role reconciliation, system-admin bootstrap |
| `src/Presentation/.../Startup.cs` | Registers the diagnostics options and the seeder, and runs the seeder |
| `tests/Infrastructure/ArturRios.IdentityManager.Data.Tests/` | **New.** Unit tests for the Data project |
| `tests/Presentation/.../Support/PostgresFixture.cs` | Migrates the throwaway container and exposes a context factory |
| `tests/Presentation/.../SchemaTests.cs` | **New.** Asserts the migrated schema's table names |
| `tests/Presentation/.../SeedingTests.cs` | **New.** Asserts roles and the system admin after the API boots |
| `scripts/migrations.py` | **New.** Menu to list, create and apply migrations |
| `scripts/test_migrations.py` | **New.** Unit tests for the script's parsing and redaction |
| `.config/dotnet-tools.json` | **New.** Pins the `dotnet-ef` tool |
| `README.md` | Documents the local `.env` bootstrap and the migration workflow |

---

### Task 1: Configuration files reach the build output

`ConfigurationLoader` resolves `Environments/.env.<environment>` (falling back to `Environments/.env.local`) and `Settings/appsettings.<environment>.json` relative to the **application base path** — `bin/Debug/net10.0/`. Neither folder is copied there today, so the connection string is never loaded from a file.

**Files:**
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/ArturRios.IdentityManager.WebApi.csproj`
- Modify: `README.md`

**Interfaces:**
- Consumes: nothing.
- Produces: `bin/Debug/net10.0/Environments/.env*` and `bin/Debug/net10.0/Settings/appsettings*.json` at runtime. No code surface.

- [ ] **Step 1: Confirm the files are missing from the output**

Run:

```bash
ls src/Presentation/ArturRios.IdentityManager.WebApi/bin/Debug/net10.0/Environments src/Presentation/ArturRios.IdentityManager.WebApi/bin/Debug/net10.0/Settings
```

Expected: both paths report "No such file or directory".

- [ ] **Step 2: Add the content items to the Web API project**

In `ArturRios.IdentityManager.WebApi.csproj`, add this `ItemGroup` immediately after the closing `</ItemGroup>` of the `ProjectReference` group:

```xml
  <ItemGroup>
    <!-- ConfigurationLoader resolves Environments/ and Settings/ relative to the application base
         path, so both folders must sit next to the built assembly. -->
    <Content Include="Environments\.env*" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="Settings\appsettings*.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 3: Rebuild and verify the files are copied**

Run:

```bash
dotnet build src/ArturRios.IdentityManager.sln -v q --nologo && ls src/Presentation/ArturRios.IdentityManager.WebApi/bin/Debug/net10.0/Environments src/Presentation/ArturRios.IdentityManager.WebApi/bin/Debug/net10.0/Settings
```

Expected: `Build succeeded.`, then `.env` listed under `Environments` and `appsettings.json` under `Settings`.

If the build fails with `NETSDK1022` (duplicate `Content` items), add `<None Remove="Environments\.env*" />` and `<None Remove="Settings\appsettings*.json" />` as the first two lines of the new `ItemGroup`.

- [ ] **Step 4: Document the local bootstrap in the README**

Replace the `## Run` section of `README.md` with:

````markdown
## Configure

`Environments/.env` is a tracked template of every variable the API reads. Real values live in
per-environment files that are gitignored. Create your local one before the first run:

```bash
cp src/Presentation/ArturRios.IdentityManager.WebApi/Environments/.env src/Presentation/ArturRios.IdentityManager.WebApi/Environments/.env.local
```

Then fill in `IDENTITY_MANAGER_DATA_CONNECTIONSTRING` (a PostgreSQL connection string),
`IDENTITY_MANAGER_DATA_DATABASETYPE` (`PostgreSql`), and the `IDENTITY_MANAGER_MASTER_USER_*`
values used to seed the first system administrator.

## Database

The schema is managed with EF Core migrations, applied explicitly — the API never migrates on
startup, and refuses to start when migrations are pending. Use the migration menu:

```bash
python scripts/migrations.py
```

It asks which environment file to load, then offers to list, create or apply migrations. The first
run needs the pinned EF tool:

```bash
dotnet tool restore
```

## Run

```bash
dotnet run --project src/Presentation/ArturRios.IdentityManager.WebApi
```
````

- [ ] **Step 5: Commit**

```bash
git add src/Presentation/ArturRios.IdentityManager.WebApi/ArturRios.IdentityManager.WebApi.csproj README.md
git commit -m "build: copy environment and settings files to the output"
```

---

### Task 2: snake_case singular naming and production-safe diagnostics

Both changes rewrite `AppDbContext.OnConfiguring`, so they land together to avoid touching the same method twice.

**Files:**
- Create: `src/Infrastructure/ArturRios.IdentityManager.Data/Configuration/DbContextDiagnosticsOptions.cs`
- Modify: `src/Infrastructure/ArturRios.IdentityManager.Data/Configuration/AppDbContext.cs`
- Modify: `src/Infrastructure/ArturRios.IdentityManager.Data/ArturRios.IdentityManager.Data.csproj`
- Modify: all nine files in `src/Infrastructure/ArturRios.IdentityManager.Data/EntityMaps/`
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `ArturRios.IdentityManager.Data.Configuration.DbContextDiagnosticsOptions` — sealed class with `bool SensitiveDataLogging { get; init; }`, `bool DetailedErrors { get; init; }`, both defaulting to `false`, and `static readonly DbContextDiagnosticsOptions Disabled`.
  - `AppDbContext(DbContextOptions options, ILoggerFactory loggerFactory, DbContextDiagnosticsOptions diagnostics)` — the third parameter is new and every caller must supply it.

- [ ] **Step 1: Add the naming-convention package**

Run:

```bash
dotnet add src/Infrastructure/ArturRios.IdentityManager.Data/ArturRios.IdentityManager.Data.csproj package EFCore.NamingConventions --version 10.0.1
```

Expected: `info : PackageReference for package 'EFCore.NamingConventions' version '10.0.1' added`.

- [ ] **Step 2: Create the diagnostics options**

Create `src/Infrastructure/ArturRios.IdentityManager.Data/Configuration/DbContextDiagnosticsOptions.cs`:

```csharp
namespace ArturRios.IdentityManager.Data.Configuration;

/// <summary>
///     Controls the EF Core diagnostics that expose data values in logs and exception messages.
///     Both flags default to <c>false</c>, so an environment we fail to classify is treated as
///     production and leaks nothing.
/// </summary>
public sealed class DbContextDiagnosticsOptions
{
    /// <summary>Diagnostics fully disabled — the production-safe default.</summary>
    public static readonly DbContextDiagnosticsOptions Disabled = new();

    /// <summary>Whether query parameter values — password hashes, salts, e-mails — may be logged.</summary>
    public bool SensitiveDataLogging { get; init; }

    /// <summary>Whether column values may be included in EF exception messages.</summary>
    public bool DetailedErrors { get; init; }
}
```

- [ ] **Step 3: Apply naming and diagnostics in the context**

In `AppDbContext.cs`, replace the class declaration and `OnConfiguring` with:

```csharp
public class AppDbContext(
    DbContextOptions options,
    ILoggerFactory loggerFactory,
    DbContextDiagnosticsOptions diagnostics) : BaseDbContext(options)
{
    private const string Schema = "identity_manager";
```

```csharp
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder
            .UseLoggerFactory(loggerFactory)
            .UseSnakeCaseNamingConvention()
            .EnableDetailedErrors(diagnostics.DetailedErrors)
            .EnableSensitiveDataLogging(diagnostics.SensitiveDataLogging);
    }
```

Leave the `DbSet` properties and `OnModelCreating` untouched. The rest of the file is unchanged.

- [ ] **Step 4: Give every entity a singular table name**

Add a `ToTable` call as the first statement of each `Configure` method, before the existing `HasKey`:

| File | Line to add |
| --- | --- |
| `PersonDbMap.cs` | `person.ToTable("person");` |
| `ScopeDbMap.cs` | `scope.ToTable("scope");` |
| `RoleDbMap.cs` | `role.ToTable("role");` |
| `ApplicationDbMap.cs` | `application.ToTable("application");` |
| `GoogleUserDbMap.cs` | `googleUser.ToTable("google_user");` |
| `ScopeOwnerDbMap.cs` | `scopeOwner.ToTable("scope_owner");` |
| `ScopeUserDbMap.cs` | `scopeUser.ToTable("scope_user");` |
| `PasswordResetTokenDbMap.cs` | `token.ToTable("password_reset_token");` |
| `EmailVerificationTokenDbMap.cs` | `token.ToTable("email_verification_token");` |

For example, `PersonDbMap.Configure` starts:

```csharp
    public static void Configure(this EntityTypeBuilder<Person> person)
    {
        person.ToTable("person");

        person.HasKey(x => x.Id);
```

- [ ] **Step 5: Pin the role key to explicit values**

In `RoleDbMap.cs`, immediately after `role.HasKey(x => x.Id);`, add:

```csharp
        // Role ids are assigned from the Roles enum by DatabaseSeeder (SystemAdmin = 1,
        // ScopeAdmin = 2, User = 3), not generated by the database. Roles are fixed reference data
        // (FR-RO-01), so there is no identity sequence left to drift out of step with the rows.
        role.Property(x => x.Id).ValueGeneratedNever();
```

- [ ] **Step 6: Register the diagnostics options in the Web API**

In `Startup.cs`, make the first two statements of `AddDependencies()`:

```csharp
    public override void AddDependencies()
    {
        // EF diagnostics expose parameter and column values, so they stay off in production.
        var diagnosticsEnabled = !Builder.Environment.IsProduction();

        Builder.Services.AddSingleton(new DbContextDiagnosticsOptions
        {
            SensitiveDataLogging = diagnosticsEnabled,
            DetailedErrors = diagnosticsEnabled
        });

        Builder.Services.AddPostgreSqlProvider();
```

The rest of the method is unchanged. If `IsProduction()` does not resolve, add `using Microsoft.Extensions.Hosting;` to the file's usings.

- [ ] **Step 7: Build**

Run:

```bash
dotnet build src/ArturRios.IdentityManager.sln -v q --nologo
```

Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 8: Commit**

```bash
git add src/Infrastructure/ArturRios.IdentityManager.Data src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs
git commit -m "feat: use snake_case singular tables and gate EF diagnostics"
```

---

### Task 3: Design-time factory, EF tool manifest and the initial migration

`dotnet ef` cannot construct `AppDbContext` — the constructor takes an `ILoggerFactory` and now a `DbContextDiagnosticsOptions`. A design-time factory supplies both.

**Files:**
- Create: `src/Infrastructure/ArturRios.IdentityManager.Data/Configuration/DesignTimeDbContextFactory.cs`
- Create: `.config/dotnet-tools.json` (generated)
- Create: `src/Infrastructure/ArturRios.IdentityManager.Data/Migrations/` (generated)
- Modify: `src/Infrastructure/ArturRios.IdentityManager.Data/ArturRios.IdentityManager.Data.csproj`

**Interfaces:**
- Consumes: `AppDbContext(DbContextOptions, ILoggerFactory, DbContextDiagnosticsOptions)` and `DbContextDiagnosticsOptions.Disabled` from Task 2.
- Produces: a migration named `InitialCreate` in the `ArturRios.IdentityManager.Data.Migrations` namespace, and `DesignTimeDbContextFactory` reading `IDENTITY_MANAGER_DATA_CONNECTIONSTRING`.

- [ ] **Step 1: Pin the EF tool**

Run:

```bash
dotnet new tool-manifest && dotnet tool install dotnet-ef --version 10.0.10
```

Expected: `The template "Dotnet local tool manifest file" was created successfully.` then `You can invoke the tool from this directory using the following commands: 'dotnet tool run dotnet-ef'`.

- [ ] **Step 2: Add the design package and runtime config generation**

Run:

```bash
dotnet add src/Infrastructure/ArturRios.IdentityManager.Data/ArturRios.IdentityManager.Data.csproj package Microsoft.EntityFrameworkCore.Design --version 10.0.10
```

Then edit the resulting `PackageReference` in `ArturRios.IdentityManager.Data.csproj` so the design package does not flow to consumers, and let the class library act as its own EF startup project:

```xml
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <!-- Lets `dotnet ef` use this class library as its own --startup-project. -->
        <GenerateRuntimeConfigurationFiles>true</GenerateRuntimeConfigurationFiles>
    </PropertyGroup>
```

```xml
      <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.10">
        <PrivateAssets>all</PrivateAssets>
        <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      </PackageReference>
```

- [ ] **Step 3: Create the design-time factory**

Create `src/Infrastructure/ArturRios.IdentityManager.Data/Configuration/DesignTimeDbContextFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.IdentityManager.Data.Configuration;

/// <summary>
///     Builds an <see cref="AppDbContext" /> for the EF Core command-line tools, which have no
///     access to the application's dependency-injection container. The connection string comes from
///     <c>IDENTITY_MANAGER_DATA_CONNECTIONSTRING</c>; <c>scripts/migrations.py</c> loads it from the
///     selected environment file before invoking <c>dotnet ef</c>. Diagnostics are disabled — design
///     time never needs them and the tools may well be pointed at production.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string ConnectionStringVariable = "IDENTITY_MANAGER_DATA_CONNECTIONSTRING";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Environment variable '{ConnectionStringVariable}' is unset. Run scripts/migrations.py, " +
                "which loads it from the environment file you select, or set it manually before " +
                "invoking dotnet ef.");
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AppDbContext(options, NullLoggerFactory.Instance, DbContextDiagnosticsOptions.Disabled);
    }
}
```

- [ ] **Step 4: Verify the factory fails loudly without a connection string**

Run:

```bash
dotnet ef migrations list --project src/Infrastructure/ArturRios.IdentityManager.Data --startup-project src/Infrastructure/ArturRios.IdentityManager.Data
```

Expected: failure mentioning `Environment variable 'IDENTITY_MANAGER_DATA_CONNECTIONSTRING' is unset`.

If instead it fails with `Unable to create a 'DbContext'` or complains the startup project is not executable, rerun with `--startup-project src/Presentation/ArturRios.IdentityManager.WebApi` and use that flag for the rest of this task and in Task 7's `DATA_PROJECT`/`STARTUP_PROJECT` constants.

- [ ] **Step 5: Generate the initial migration**

The connection string only has to be well-formed — EF does not connect to scaffold a migration.

In bash:

```bash
IDENTITY_MANAGER_DATA_CONNECTIONSTRING="Host=localhost;Database=identity_manager;Username=postgres;Password=postgres" dotnet ef migrations add InitialCreate --project src/Infrastructure/ArturRios.IdentityManager.Data --startup-project src/Infrastructure/ArturRios.IdentityManager.Data --output-dir Migrations
```

In PowerShell, which has no inline environment-variable prefix, set it first:

```bash
$env:IDENTITY_MANAGER_DATA_CONNECTIONSTRING = "Host=localhost;Database=identity_manager;Username=postgres;Password=postgres"; dotnet ef migrations add InitialCreate --project src/Infrastructure/ArturRios.IdentityManager.Data --startup-project src/Infrastructure/ArturRios.IdentityManager.Data --output-dir Migrations
```

Expected: `Build succeeded.` then `Done. To undo this action, use 'ef migrations remove'`.

- [ ] **Step 6: Verify the generated names**

Run:

```bash
grep -E 'CreateTable|name: "(person|scope|role|google_user|scope_owner|scope_user|application|password_reset_token|email_verification_token)"|public_id|created_at' src/Infrastructure/ArturRios.IdentityManager.Data/Migrations/*_InitialCreate.cs | head -40
```

Expected: nine `CreateTable` calls with the singular snake_case names from Task 2's table, and snake_case columns such as `public_id` and `created_at`. If any table is still plural or PascalCase, the `ToTable` call or `UseSnakeCaseNamingConvention()` is missing — fix it, delete the `Migrations` folder, and redo Step 5.

Also confirm the role key is not an identity column:

```bash
grep -A 3 'name: "role"' src/Infrastructure/ArturRios.IdentityManager.Data/Migrations/*_InitialCreate.cs
```

Expected: the `id` column has **no** `.Annotation("Npgsql:ValueGenerationStrategy", ...)`.

- [ ] **Step 7: Commit**

```bash
git add .config src/Infrastructure/ArturRios.IdentityManager.Data
git commit -m "feat: add design-time factory and the initial migration"
```

---

### Task 4: The functional fixture migrates its container

`HealthCheckTests` is red today because it never joins `FunctionalCollection`, so no container starts and the data configuration finds no environment variables. This task turns it green and proves the migration applies.

**Requires Docker.** Run `docker version` first; start Docker Desktop if it errors.

**Files:**
- Modify: `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/Support/PostgresFixture.cs`
- Modify: `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/HealthCheckTests.cs`
- Create: `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/SchemaTests.cs`

**Interfaces:**
- Consumes: `AppDbContext(DbContextOptions, ILoggerFactory, DbContextDiagnosticsOptions)`, `DbContextDiagnosticsOptions.Disabled`, and the `InitialCreate` migration.
- Produces: `PostgresFixture.ConnectionString` (existing) and `PostgresFixture.CreateContext()` returning `AppDbContext` bound to the container — Task 6 uses both.

- [ ] **Step 1: Confirm the existing failure**

Run:

```bash
dotnet test tests/Presentation/ArturRios.IdentityManager.WebApi.Tests --nologo -v q
```

Expected: `Failed: 1` with `DataAccessException : Environment variable 'IDENTITY_MANAGER_DATA_DATABASETYPE' is unset`.

- [ ] **Step 2: Write the failing schema test**

Create `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/SchemaTests.cs`:

```csharp
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Npgsql;

namespace ArturRios.IdentityManager.WebApi.Tests;

/// <summary>
///     Verifies that the migration applied to the throwaway container produces the schema the
///     design calls for: everything under <c>identity_manager</c>, singular snake_case table names.
/// </summary>
[Collection(nameof(FunctionalCollection))]
public class SchemaTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenMigrationsApplied_WhenSchemaInspected_ThenTablesAreSingularSnakeCase()
    {
        string[] expected =
        [
            "application",
            "email_verification_token",
            "google_user",
            "password_reset_token",
            "person",
            "role",
            "scope",
            "scope_owner",
            "scope_user"
        ];

        var actual = await ReadTableNamesAsync();

        Assert.Equal(expected, actual);
    }

    private async Task<List<string>> ReadTableNamesAsync()
    {
        var names = new List<string>();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            select table_name
            from information_schema.tables
            where table_schema = 'identity_manager' and table_name <> '__EFMigrationsHistory'
            order by table_name
            """,
            connection);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
```

- [ ] **Step 3: Run it to verify it fails**

Run:

```bash
dotnet test tests/Presentation/ArturRios.IdentityManager.WebApi.Tests --nologo -v q --filter "FullyQualifiedName~SchemaTests"
```

Expected: FAIL — the container starts but has no tables, so the assertion reports an empty actual list.

- [ ] **Step 4: Make the fixture migrate the container**

Replace the whole body of `PostgresFixture.cs` with:

```csharp
using ArturRios.IdentityManager.Data.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace ArturRios.IdentityManager.WebApi.Tests.Support;

/// <summary>
///     Starts a throwaway PostgreSQL container once for the whole functional test suite, applies the
///     EF migrations to it, and exposes its connection string, so functional tests run end-to-end
///     against a real database that closely matches production. Shared via
///     <see cref="FunctionalCollection" /> so the container is created once, not per test class.
///     See docs/Testing Specification Document.md §7.2.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string ConnectionStringVariable = "IDENTITY_MANAGER_DATA_CONNECTIONSTRING";
    private const string DatabaseTypeVariable = "IDENTITY_MANAGER_DATA_DATABASETYPE";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    /// <summary>The connection string of the running container's database.</summary>
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Point the API under test at the container instead of a developer's local database.
        Environment.SetEnvironmentVariable(ConnectionStringVariable, ConnectionString);
        Environment.SetEnvironmentVariable(DatabaseTypeVariable, "PostgreSql");

        // The API refuses to start against a schema with pending migrations, so apply them here.
        await using var context = CreateContext();

        await context.Database.MigrateAsync();
    }

    /// <summary>
    ///     Creates a context bound to the container, for tests that assert on database state
    ///     directly rather than through the API.
    /// </summary>
    public AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options,
        NullLoggerFactory.Instance,
        DbContextDiagnosticsOptions.Disabled);

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
```

- [ ] **Step 5: Join `HealthCheckTests` to the collection**

The class boots the API, which needs the container's environment variables, so it must belong to the collection. Replace the `using` block and class declaration in `HealthCheckTests.cs`:

```csharp
using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.IdentityManager.WebApi.Tests;

// The fixture parameter is unused, but taking it makes the dependency on the migrated container
// explicit — xUnit builds the collection fixture before constructing this class, and the base
// constructor boots the API, which reads the fixture's environment variables.
[Collection(nameof(FunctionalCollection))]
public class HealthCheckTests(PostgresFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
```

Leave the test method and the `HealthCheckRoute` constant unchanged.

An unread primary-constructor parameter produces no warning, so nothing further is needed here.

- [ ] **Step 6: Run the suite to verify both tests pass**

Run:

```bash
dotnet test tests/Presentation/ArturRios.IdentityManager.WebApi.Tests --nologo -v q
```

Expected: `Passed: 2, Failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add tests/Presentation/ArturRios.IdentityManager.WebApi.Tests
git commit -m "test: migrate the functional container and assert the schema"
```

---

### Task 5: Master user options and the Data test project

The Data project has no test project yet; the Testing Specification requires one per production project. `MasterUserOptions` is the piece of the seeder that is testable without a database.

**Files:**
- Create: `src/Infrastructure/ArturRios.IdentityManager.Data/Seeding/MasterUserOptions.cs`
- Create: `tests/Infrastructure/ArturRios.IdentityManager.Data.Tests/ArturRios.IdentityManager.Data.Tests.csproj`
- Create: `tests/Infrastructure/ArturRios.IdentityManager.Data.Tests/Seeding/MasterUserOptionsTests.cs`
- Modify: `src/ArturRios.IdentityManager.sln`

**Interfaces:**
- Consumes: nothing.
- Produces: `ArturRios.IdentityManager.Data.Seeding.MasterUserOptions` — `sealed record MasterUserOptions(string Name, string Email, string Password)` with `const string NameVariable/EmailVariable/PasswordVariable`, `static MasterUserOptions FromEnvironment()`, and `bool IsComplete`. Tasks 6 uses all of them.

- [ ] **Step 1: Create the test project and add it to the solution**

Run:

```bash
dotnet new xunit -o tests/Infrastructure/ArturRios.IdentityManager.Data.Tests -n ArturRios.IdentityManager.Data.Tests --force
```

```bash
dotnet sln src/ArturRios.IdentityManager.sln add tests/Infrastructure/ArturRios.IdentityManager.Data.Tests/ArturRios.IdentityManager.Data.Tests.csproj --solution-folder "Tests/Infrastructure"
```

Expected: `Project ... added to the solution.`

- [ ] **Step 2: Replace the generated project file**

Overwrite `tests/Infrastructure/ArturRios.IdentityManager.Data.Tests/ArturRios.IdentityManager.Data.Tests.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <IsPackable>false</IsPackable>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="ArturRios.Util.Test" Version="2.0.0" />
        <PackageReference Include="coverlet.collector" Version="10.0.1">
          <PrivateAssets>all</PrivateAssets>
          <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
        </PackageReference>
        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.8.1" />
        <PackageReference Include="xunit" Version="2.9.3"/>
        <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5">
          <PrivateAssets>all</PrivateAssets>
          <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
        </PackageReference>
    </ItemGroup>

    <ItemGroup>
        <Using Include="Xunit"/>
    </ItemGroup>

    <ItemGroup>
      <ProjectReference Include="..\..\..\src\Infrastructure\ArturRios.IdentityManager.Data\ArturRios.IdentityManager.Data.csproj" />
    </ItemGroup>

</Project>
```

Then delete the template's placeholder test:

```bash
rm -f tests/Infrastructure/ArturRios.IdentityManager.Data.Tests/UnitTest1.cs
```

- [ ] **Step 3: Write the failing tests**

Create `tests/Infrastructure/ArturRios.IdentityManager.Data.Tests/Seeding/MasterUserOptionsTests.cs`:

```csharp
using ArturRios.IdentityManager.Data.Seeding;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.IdentityManager.Data.Tests.Seeding;

public class MasterUserOptionsTests
{
    [UnitFact]
    public void GivenAllValuesPresent_WhenCompletenessChecked_ThenOptionsAreComplete()
    {
        var options = new MasterUserOptions("Master User", "master@identity-manager.test", "Str0ng-Pass!");

        Assert.True(options.IsComplete);
    }

    [UnitTheory]
    [InlineData("", "master@identity-manager.test", "Str0ng-Pass!")]
    [InlineData("Master User", "", "Str0ng-Pass!")]
    [InlineData("Master User", "master@identity-manager.test", "")]
    [InlineData("   ", "master@identity-manager.test", "Str0ng-Pass!")]
    public void GivenAMissingValue_WhenCompletenessChecked_ThenOptionsAreIncomplete(
        string name,
        string email,
        string password)
    {
        var options = new MasterUserOptions(name, email, password);

        Assert.False(options.IsComplete);
    }

    [UnitFact]
    public void GivenUnsetVariables_WhenReadFromEnvironment_ThenValuesAreEmptyAndIncomplete()
    {
        Environment.SetEnvironmentVariable(MasterUserOptions.NameVariable, null);
        Environment.SetEnvironmentVariable(MasterUserOptions.EmailVariable, null);
        Environment.SetEnvironmentVariable(MasterUserOptions.PasswordVariable, null);

        var options = MasterUserOptions.FromEnvironment();

        Assert.Equal(string.Empty, options.Name);
        Assert.Equal(string.Empty, options.Email);
        Assert.Equal(string.Empty, options.Password);
        Assert.False(options.IsComplete);
    }
}
```

- [ ] **Step 4: Run to verify they fail**

Run:

```bash
dotnet test tests/Infrastructure/ArturRios.IdentityManager.Data.Tests --nologo -v q
```

Expected: build failure — `The type or namespace name 'MasterUserOptions' could not be found`.

- [ ] **Step 5: Implement `MasterUserOptions`**

Create `src/Infrastructure/ArturRios.IdentityManager.Data/Seeding/MasterUserOptions.cs`:

```csharp
namespace ArturRios.IdentityManager.Data.Seeding;

/// <summary>
///     Credentials for the master system administrator, read from the
///     <c>IDENTITY_MANAGER_MASTER_USER_*</c> environment variables. They are used only when the
///     database holds no system administrator yet — see
///     <see cref="DatabaseSeeder" />.
/// </summary>
public sealed record MasterUserOptions(string Name, string Email, string Password)
{
    /// <summary>Environment variable holding the master user's display name.</summary>
    public const string NameVariable = "IDENTITY_MANAGER_MASTER_USER_NAME";

    /// <summary>Environment variable holding the master user's e-mail address.</summary>
    public const string EmailVariable = "IDENTITY_MANAGER_MASTER_USER_EMAIL";

    /// <summary>Environment variable holding the master user's plain-text password.</summary>
    public const string PasswordVariable = "IDENTITY_MANAGER_MASTER_USER_PASSWORD";

    /// <summary>Whether all three values are present, so a master user could be created.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Name) &&
        !string.IsNullOrWhiteSpace(Email) &&
        !string.IsNullOrWhiteSpace(Password);

    /// <summary>Reads the three variables from the current process environment.</summary>
    public static MasterUserOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable(NameVariable) ?? string.Empty,
        Environment.GetEnvironmentVariable(EmailVariable) ?? string.Empty,
        Environment.GetEnvironmentVariable(PasswordVariable) ?? string.Empty);
}
```

The `<see cref="DatabaseSeeder" />` reference resolves in Task 6. Until then the build may emit CS1574; if it does, temporarily write the reference as `<c>DatabaseSeeder</c>` and restore the `see cref` in Task 6.

- [ ] **Step 6: Run to verify they pass**

Run:

```bash
dotnet test tests/Infrastructure/ArturRios.IdentityManager.Data.Tests --nologo -v q
```

Expected: `Passed: 6, Failed: 0` — two facts plus the four theory cases, each counted individually.

- [ ] **Step 7: Commit**

```bash
git add src/ArturRios.IdentityManager.sln src/Infrastructure/ArturRios.IdentityManager.Data/Seeding tests/Infrastructure
git commit -m "feat: add master user options with unit tests"
```

---

### Task 6: The database seeder

**Requires Docker.**

**Files:**
- Create: `src/Infrastructure/ArturRios.IdentityManager.Data/Seeding/DatabaseSeeder.cs`
- Modify: `src/Infrastructure/ArturRios.IdentityManager.Data/ArturRios.IdentityManager.Data.csproj`
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs`
- Modify: `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/Support/PostgresFixture.cs`
- Create: `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/SeedingTests.cs`

**Interfaces:**
- Consumes: `MasterUserOptions` (Task 5), `PostgresFixture.CreateContext()` (Task 4), `AppDbContext`.
- Produces: `ArturRios.IdentityManager.Data.Seeding.DatabaseSeeder` with constructor `(AppDbContext context, MasterUserOptions masterUser, ILogger<DatabaseSeeder> logger)` and `Task SeedAsync(CancellationToken cancellationToken = default)`. Also `PostgresFixture.MasterUserEmail`.

- [ ] **Step 1: Add the hashing package**

Run:

```bash
dotnet add src/Infrastructure/ArturRios.IdentityManager.Data/ArturRios.IdentityManager.Data.csproj package ArturRios.Util --version 1.4.0
```

- [ ] **Step 2: Set the master-user variables in the fixture**

In `PostgresFixture.cs`, add the constant below the two existing `private const string` lines:

```csharp
    /// <summary>E-mail of the master system administrator the API seeds into the container.</summary>
    public const string MasterUserEmail = "master@identity-manager.test";
```

and add these three lines in `InitializeAsync`, immediately after the `DatabaseTypeVariable` assignment:

```csharp
        // The seeder refuses to start without a configured master user.
        Environment.SetEnvironmentVariable(MasterUserOptions.NameVariable, "Master User");
        Environment.SetEnvironmentVariable(MasterUserOptions.EmailVariable, MasterUserEmail);
        Environment.SetEnvironmentVariable(MasterUserOptions.PasswordVariable, "Str0ng-Master-Pass!");
```

Add `using ArturRios.IdentityManager.Data.Seeding;` to the file's usings.

- [ ] **Step 3: Write the failing seeding tests**

Create `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/SeedingTests.cs`:

```csharp
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.WebApi.Tests;

/// <summary>
///     The API seeds reference data as it starts. Constructing this class boots the API against the
///     migrated container, so each test asserts the database state the seeder left behind.
/// </summary>
[Collection(nameof(FunctionalCollection))]
public class SeedingTests(PostgresFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenApiStarted_WhenRolesRead_ThenEveryEnumMemberIsStoredWithItsEnumId()
    {
        await using var context = fixture.CreateContext();

        var roles = await context.Roles.OrderBy(x => x.Id).ToListAsync();

        Assert.Equal(3, roles.Count);
        Assert.Equal(new[] { 1L, 2L, 3L }, roles.Select(x => x.Id).ToArray());
        Assert.Equal(
            new[] { nameof(Roles.SystemAdmin), nameof(Roles.ScopeAdmin), nameof(Roles.User) },
            roles.Select(x => x.Name).ToArray());
        Assert.All(roles, role => Assert.False(string.IsNullOrWhiteSpace(role.Description)));
    }

    [FunctionalFact]
    public async Task GivenApiStarted_WhenSystemAdminsRead_ThenTheMasterUserExistsWithAHashedPassword()
    {
        await using var context = fixture.CreateContext();

        var admins = await context.Persons
            .Where(x => x.RoleId == (long)Roles.SystemAdmin && !x.IsDeleted)
            .ToListAsync();

        var master = Assert.Single(admins);

        Assert.Equal(PostgresFixture.MasterUserEmail, master.Email);
        Assert.True(master.EmailVerified);
        Assert.NotEmpty(master.PasswordHash);
        Assert.NotEmpty(master.Salt);
    }

    [FunctionalFact]
    public async Task GivenSeedingAlreadyRan_WhenSeederRunsAgain_ThenNoDuplicateRowsAreCreated()
    {
        await using var context = fixture.CreateContext();

        var seeder = new DatabaseSeeder(
            context,
            new MasterUserOptions("Master User", PostgresFixture.MasterUserEmail, "Str0ng-Master-Pass!"),
            NullLogger<DatabaseSeeder>.Instance);

        await seeder.SeedAsync();

        Assert.Equal(3, await context.Roles.CountAsync());
        Assert.Equal(1, await context.Persons.CountAsync(x => x.RoleId == (long)Roles.SystemAdmin));
    }
}
```

This file needs two more usings beyond those listed above — add them to the block at the top:

```csharp
using ArturRios.IdentityManager.Data.Seeding;
using Microsoft.Extensions.Logging.Abstractions;
```

- [ ] **Step 4: Run to verify they fail**

Run:

```bash
dotnet test tests/Presentation/ArturRios.IdentityManager.WebApi.Tests --nologo -v q --filter "FullyQualifiedName~SeedingTests"
```

Expected: build failure — `The type or namespace name 'DatabaseSeeder' could not be found`. That is the failing state for this task; Step 5 introduces the type.

- [ ] **Step 5: Implement the seeder**

Create `src/Infrastructure/ArturRios.IdentityManager.Data/Seeding/DatabaseSeeder.cs`:

```csharp
using System.ComponentModel;
using System.Reflection;
using ArturRios.IdentityManager.Data.Configuration;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.Util.Hashing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArturRios.IdentityManager.Data.Seeding;

/// <summary>
///     Brings a migrated database to the state the application assumes: every <see cref="Roles" />
///     member present as a row, and at least one system administrator to sign in as. Idempotent, so
///     it runs on every startup. It never applies migrations — that is
///     <c>scripts/migrations.py</c>'s job — and refuses to seed a schema that is behind.
/// </summary>
public class DatabaseSeeder(
    AppDbContext context,
    MasterUserOptions masterUser,
    ILogger<DatabaseSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaIsUpToDateAsync(cancellationToken);
        await EnsureRolesAsync(cancellationToken);
        await EnsureSystemAdminAsync(cancellationToken);
    }

    private async Task EnsureSchemaIsUpToDateAsync(CancellationToken cancellationToken)
    {
        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

        if (pending.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", pending);

        logger.LogCritical("Database is behind by {Count} migration(s): {Migrations}", pending.Count, names);

        throw new InvalidOperationException(
            $"The database is missing {pending.Count} migration(s): {names}. Apply them with " +
            "scripts/migrations.py before starting the API.");
    }

    private async Task EnsureRolesAsync(CancellationToken cancellationToken)
    {
        var stored = await context.Roles.ToDictionaryAsync(x => x.Id, cancellationToken);

        foreach (var role in Enum.GetValues<Roles>())
        {
            var id = (long)role;
            var name = role.ToString();
            var description = DescriptionOf(role);

            if (!stored.TryGetValue(id, out var existing))
            {
                context.Roles.Add(new Role { Id = id, Name = name, Description = description });

                logger.LogInformation("Seeding role {RoleName} with id {RoleId}", name, id);

                continue;
            }

            if (existing.Name == name && existing.Description == description)
            {
                continue;
            }

            existing.Name = name;
            existing.Description = description;

            logger.LogInformation("Realigning role {RoleId} with the Roles enum", id);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureSystemAdminAsync(CancellationToken cancellationToken)
    {
        const long systemAdminRoleId = (long)Roles.SystemAdmin;

        var exists = await context.Persons
            .AnyAsync(x => x.RoleId == systemAdminRoleId && !x.IsDeleted, cancellationToken);

        if (exists)
        {
            return;
        }

        if (!masterUser.IsComplete)
        {
            throw new InvalidOperationException(
                "The database has no system administrator and the master user is not configured. Set " +
                $"{MasterUserOptions.NameVariable}, {MasterUserOptions.EmailVariable} and " +
                $"{MasterUserOptions.PasswordVariable} before starting the API.");
        }

        var passwordHash = Hash.EncodeWithRandomSalt(masterUser.Password, out var salt);

        context.Persons.Add(new Person
        {
            Name = masterUser.Name,
            Email = masterUser.Email,
            PasswordHash = passwordHash,
            Salt = salt,
            EmailVerified = true,
            RoleId = systemAdminRoleId
        });

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Seeded the master system administrator {Email}", masterUser.Email);
    }

    private static string DescriptionOf(Roles role) =>
        typeof(Roles).GetField(role.ToString())?.GetCustomAttribute<DescriptionAttribute>()?.Description
        ?? role.ToString();
}
```

- [ ] **Step 6: Wire the seeder into startup**

In `Startup.cs`, add to the usings:

```csharp
using ArturRios.IdentityManager.Data.Seeding;
```

Register it at the end of `AddDependencies()`:

```csharp
        Builder.Services.AddSingleton(MasterUserOptions.FromEnvironment());
        Builder.Services.AddScoped<DatabaseSeeder>();
```

Then override `StartServices()` — the hook `Build()` already calls after `ConfigureApp()`:

```csharp
    public override void StartServices()
    {
        using var scope = App.Services.CreateScope();

        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();

        seeder.SeedAsync().GetAwaiter().GetResult();
    }
```

Place it after `ConfigureWebApi()` and before `ConfigureLogging()`. If `StartServices` is not `virtual` in `WebApiStartup`, call `SeedDatabase()` directly from `Build()` in place of the existing `StartServices();` line and name the method `SeedDatabase`.

- [ ] **Step 7: Run the seeding tests to verify they pass**

Run:

```bash
dotnet test tests/Presentation/ArturRios.IdentityManager.WebApi.Tests --nologo -v q
```

Expected: `Passed: 5, Failed: 0` — the health check, the schema test, and three seeding tests.

- [ ] **Step 8: Run the whole suite**

Run:

```bash
dotnet test src/ArturRios.IdentityManager.sln --nologo -v q
```

Expected: no failures across all five test projects.

- [ ] **Step 9: Commit**

```bash
git add src/Infrastructure/ArturRios.IdentityManager.Data src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs tests/Presentation/ArturRios.IdentityManager.WebApi.Tests
git commit -m "feat: seed roles and the master system admin on startup"
```

---

### Task 7: The migration script

**Files:**
- Create: `scripts/migrations.py`
- Create: `scripts/test_migrations.py`

**Interfaces:**
- Consumes: the `Migrations` folder and `DesignTimeDbContextFactory` from Task 3, and the `.config/dotnet-tools.json` manifest.
- Produces: `parse_env_file(text) -> dict[str, str]` and `describe_connection(connection_string) -> str`, both pure and covered by `scripts/test_migrations.py`.

- [ ] **Step 1: Write the failing tests**

Create `scripts/test_migrations.py`:

```python
"""Unit tests for the pure helpers in migrations.py.

Run from the repository root:  python -m unittest discover -s scripts -p "test_*.py"
"""

import unittest

from migrations import describe_connection, parse_env_file


class ParseEnvFileTests(unittest.TestCase):
    def test_given_quoted_values_when_parsed_then_quotes_are_stripped(self):
        parsed = parse_env_file('IDENTITY_MANAGER_DATA_DATABASETYPE="PostgreSql"\n')

        self.assertEqual({"IDENTITY_MANAGER_DATA_DATABASETYPE": "PostgreSql"}, parsed)

    def test_given_a_byte_order_mark_when_parsed_then_the_first_key_is_clean(self):
        parsed = parse_env_file("\ufeffFIRST=1\n")

        self.assertEqual({"FIRST": "1"}, parsed)

    def test_given_comments_and_blank_lines_when_parsed_then_they_are_skipped(self):
        parsed = parse_env_file("# a comment\n\n  \nKEY=value\n")

        self.assertEqual({"KEY": "value"}, parsed)

    def test_given_a_value_containing_equals_when_parsed_then_the_value_is_intact(self):
        parsed = parse_env_file('CONN="Host=localhost;Database=identity_manager"\n')

        self.assertEqual({"CONN": "Host=localhost;Database=identity_manager"}, parsed)

    def test_given_a_line_without_a_separator_when_parsed_then_it_is_ignored(self):
        parsed = parse_env_file("NOT_A_PAIR\nKEY=value\n")

        self.assertEqual({"KEY": "value"}, parsed)


class DescribeConnectionTests(unittest.TestCase):
    def test_given_a_password_when_described_then_it_is_masked(self):
        described = describe_connection("Host=localhost;Database=im;Username=app;Password=secret")

        self.assertEqual("Host=localhost;Database=im;Username=app;Password=***", described)

    def test_given_a_password_in_mixed_case_when_described_then_it_is_masked(self):
        described = describe_connection("Host=localhost;PASSWORD=secret")

        self.assertEqual("Host=localhost;PASSWORD=***", described)

    def test_given_a_trailing_separator_when_described_then_no_empty_segment_remains(self):
        described = describe_connection("Host=localhost;")

        self.assertEqual("Host=localhost", described)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run to verify they fail**

Run:

```bash
python -m unittest discover -s scripts -p "test_*.py"
```

Expected: `ModuleNotFoundError: No module named 'migrations'`.

- [ ] **Step 3: Write the script**

Create `scripts/migrations.py`:

```python
#!/usr/bin/env python3
"""Interactive menu for the Identity Manager's EF Core migrations.

Lists, creates and applies migrations for ArturRios.IdentityManager.Data, loading the
connection string from one of the environment files under the Web API's Environments folder.

Usage (from anywhere in the repository):

    python scripts/migrations.py

Requires the pinned EF tool -- run `dotnet tool restore` once after cloning.
"""

import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
ENVIRONMENTS_DIR = REPO_ROOT / "src/Presentation/ArturRios.IdentityManager.WebApi/Environments"
DATA_PROJECT = REPO_ROOT / "src/Infrastructure/ArturRios.IdentityManager.Data"
STARTUP_PROJECT = DATA_PROJECT

CONNECTION_STRING_VARIABLE = "IDENTITY_MANAGER_DATA_CONNECTIONSTRING"
SECRET_KEYS = {"password", "pwd"}
MIGRATION_NAME_PATTERN = re.compile(r"^[A-Za-z][A-Za-z0-9]*$")


def parse_env_file(text):
    """Parse .env content into a dict, tolerating a BOM, quotes, comments and blank lines."""
    values = {}

    for raw_line in text.lstrip("\ufeff").splitlines():
        line = raw_line.strip()

        if not line or line.startswith("#"):
            continue

        key, separator, value = line.partition("=")

        if not separator:
            continue

        key = key.strip()
        value = value.strip()

        if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
            value = value[1:-1]

        values[key] = value

    return values


def describe_connection(connection_string):
    """Render a connection string with its secrets masked, so it is safe to print."""
    segments = []

    for raw_segment in connection_string.split(";"):
        segment = raw_segment.strip()

        if not segment:
            continue

        key, separator, _ = segment.partition("=")

        if separator and key.strip().lower() in SECRET_KEYS:
            segments.append(f"{key.strip()}=***")
        else:
            segments.append(segment)

    return ";".join(segments)


def prompt_yes_no(question):
    return input(f"{question} [y/N] ").strip().lower() in {"y", "yes"}


def choose_environment_file():
    """Ask which environment file to load. Returns a Path, or None when we cannot continue."""
    if not ENVIRONMENTS_DIR.is_dir():
        print(f"Environments folder not found: {ENVIRONMENTS_DIR}")
        return None

    files = sorted(path for path in ENVIRONMENTS_DIR.glob(".env*") if path.is_file())

    if not files:
        print(f"No environment files found in {ENVIRONMENTS_DIR}.")
        return None

    if [path.name for path in files] == [".env"]:
        template = files[0]
        print(f"Only the tracked template {template.name} exists; it holds placeholders, not real values.")

        if prompt_yes_no("Create .env.local from it now?"):
            local = ENVIRONMENTS_DIR / ".env.local"
            shutil.copyfile(template, local)
            print(f"Created {local}. Fill in the values, then run this script again.")

        return None

    print("\nEnvironment files:")

    for index, path in enumerate(files, start=1):
        print(f"  {index}) {path.name}")

    choice = input("Choose an environment file: ").strip()

    if not choice.isdigit() or not 1 <= int(choice) <= len(files):
        print("Invalid choice.")
        return None

    return files[int(choice) - 1]


def ensure_ef_tool(environ):
    result = subprocess.run(
        ["dotnet", "ef", "--version"],
        cwd=REPO_ROOT,
        env=environ,
        capture_output=True,
        text=True,
    )

    if result.returncode != 0:
        print("dotnet ef is not available. Install the pinned tool with:\n\n    dotnet tool restore\n")
        return False

    print(f"Using dotnet ef {result.stdout.strip().splitlines()[-1]}")

    return True


def run_ef(arguments, environ):
    command = [
        "dotnet",
        "ef",
        *arguments,
        "--project",
        str(DATA_PROJECT),
        "--startup-project",
        str(STARTUP_PROJECT),
    ]

    print(f"\n$ {' '.join(command)}\n")

    result = subprocess.run(command, cwd=REPO_ROOT, env=environ)

    if result.returncode != 0:
        print(f"\ndotnet ef exited with code {result.returncode}.")

    return result.returncode


def create_migration(environ):
    name = input("Migration name (PascalCase, letters and digits only): ").strip()

    if not MIGRATION_NAME_PATTERN.match(name):
        print("Invalid name. Use letters and digits, starting with a letter -- for example AddScopeIndex.")
        return

    run_ef(["migrations", "add", name, "--output-dir", "Migrations"], environ)


def apply_migrations(environ, connection_string):
    print(f"\nTarget: {describe_connection(connection_string)}")

    if not prompt_yes_no("Apply all pending migrations to this database?"):
        print("Cancelled.")
        return

    run_ef(["database", "update"], environ)


def main():
    environment_file = choose_environment_file()

    if environment_file is None:
        return 1

    variables = parse_env_file(environment_file.read_text(encoding="utf-8"))
    connection_string = variables.get(CONNECTION_STRING_VARIABLE, "")

    if not connection_string.strip():
        print(f"{environment_file.name} does not set {CONNECTION_STRING_VARIABLE}.")
        return 1

    environ = {**os.environ, **variables}

    print(f"\nLoaded {environment_file.name} -> {describe_connection(connection_string)}")

    if not ensure_ef_tool(environ):
        return 1

    while True:
        print("\n  1) List migrations")
        print("  2) Create a migration")
        print("  3) Apply migrations")
        print("  4) Exit")

        choice = input("Choose an option: ").strip()

        if choice == "1":
            run_ef(["migrations", "list"], environ)
        elif choice == "2":
            create_migration(environ)
        elif choice == "3":
            apply_migrations(environ, connection_string)
        elif choice == "4":
            return 0
        else:
            print("Unknown option.")


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:

```bash
python -m unittest discover -s scripts -p "test_*.py" -v
```

Expected: `Ran 8 tests` and `OK`.

- [ ] **Step 5: Exercise the menu end to end**

Start a local PostgreSQL (any instance you can reach), make sure `Environments/.env.local` exists with a matching `IDENTITY_MANAGER_DATA_CONNECTIONSTRING`, then run:

```bash
python scripts/migrations.py
```

Verify all three paths:
1. Choose `.env.local`, then option **1** — expected: `InitialCreate` listed.
2. Option **3**, confirm with `y` — expected: `Applying migration '..._InitialCreate'.` then `Done.` The printed target must show `Password=***`.
3. Option **1** again — expected: `InitialCreate` now marked as applied.

Then confirm the API starts against that database:

```bash
dotnet run --project src/Presentation/ArturRios.IdentityManager.WebApi
```

Expected: the log shows `Seeding role SystemAdmin with id 1` (first run only) and `Ready to run!`.

- [ ] **Step 6: Commit**

```bash
git add scripts
git commit -m "feat: add migration menu script"
```

---

## Final verification

- [ ] `dotnet build src/ArturRios.IdentityManager.sln` — succeeds with 0 warnings.
- [ ] `dotnet test src/ArturRios.IdentityManager.sln` — all tests pass (Docker running).
- [ ] `python -m unittest discover -s scripts -p "test_*.py"` — `OK`.
- [ ] `git status` — clean; `Migrations/`, `scripts/` and `.config/dotnet-tools.json` are tracked, `Environments/.env.local` is not.
