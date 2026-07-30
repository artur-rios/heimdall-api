# Testing Specification Document — Identity Manager API

## 1. Purpose

This document defines **how a use case is tested once it has been implemented**. It is a standard
to be followed by any human or agent that builds tests for this project. Following it guarantees
that every use case (UC-01 … UC-29 in the [Use Case Specification Document](Use%20Case%20Specification%20Document.md))
receives the same shape of testing, with the same tools, naming, and structure.

The rule is simple:

> **After a use case is developed, tests are built for it in the same change — before the use case
> is considered done.** A use case without its tests is an incomplete use case.

## 2. Testing philosophy

1. **Behavior-driven.** Tests describe *behavior*, not implementation. Every test is written in the
   **Given / When / Then** style and is named accordingly.
2. **Test at the right layer.** Business logic (Command/Query handlers and any Domain class that
   implements behavior) is covered by **unit tests**. The Presentation layer (the Web API) is
   covered by **functional (end-to-end) tests**.
3. **Isolation in unit tests.** A unit test exercises *one* class. Every external dependency of that
   class is replaced by a test double (fake/mock). Only the behavior of the method under test is
   asserted.
4. **Realism in functional tests.** A functional test exercises the API exactly as a client would —
   over HTTP, against a **real PostgreSQL database** provisioned on the fly with Testcontainers.
   Both the **API response** and the **resulting database state** are asserted.
5. **Same pattern every time.** The workflow in §9 is applied identically to every use case, so the
   test suite stays predictable as the system grows.

## 3. What to test for each use case

When a use case is implemented, walk this decision list and produce every applicable test:

| Artifact produced by the use case | Test kind | Test project |
| --- | --- | --- |
| A **Command** handler (`*CommandHandler`) | Unit | `*.Command.Tests` |
| A **Query** handler (`*QueryHandler`) | Unit | `*.Query.Tests` |
| Input **validators** (`*Validator`) | Unit (alongside the handler that uses them) | `*.Command.Tests` / `*.Query.Tests` |
| A **Domain** class that implements behavior (methods with logic, invariants, calculations) | Unit | `*.Domain.Tests` |
| A **Controller** endpoint exposing the use case | Functional | `*.WebApi.Tests` |

Notes:

- **Anemic domain entities** (plain data holders with only properties and navigation collections,
  e.g. [`Scope`](../src/Domain/ArturRios.IdentityManager.Domain/Entities/Scope.cs)) carry no
  behavior and therefore get **no unit tests** — their behavior is observed through the handlers and
  functional tests. A domain class earns its own unit tests the moment it gains a method that makes
  a decision (guard clause, state transition, hashing, token validity, etc.).
- Every use case that reaches the API **must** have functional coverage, even when its handler is
  already unit-tested. The two layers verify different things (see §6 and §7).

## 4. Solution & project layout

Tests live under a top-level `tests/` directory that **mirrors** the `src/` layer folders. Each
production project has exactly one corresponding test project, named by appending **`.Tests`** to
the project name. Each production class has one corresponding test class, named by appending
**`Tests`** to the class name.

```
src/
  Application/
    ArturRios.IdentityManager.Command/           →  CreateScopeCommandHandler.cs
    ArturRios.IdentityManager.Query/             →  GetScopeByIdQueryHandler.cs
    ArturRios.IdentityManager.Shared/            →  ScopeOwnershipChecker.cs
  Domain/
    ArturRios.IdentityManager.Domain/
  Infrastructure/
    ArturRios.IdentityManager.Data/              →  Seeding/MasterUserOptions.cs
  Presentation/
    ArturRios.IdentityManager.WebApi/            →  ScopeController.cs

tests/
  Application/
    ArturRios.IdentityManager.Command.Tests/     →  CreateScopeCommandHandlerTests.cs
    ArturRios.IdentityManager.Query.Tests/       →  GetScopeByIdQueryHandlerTests.cs
    ArturRios.IdentityManager.Shared.Tests/      →  ScopeOwnershipCheckerTests.cs
  Domain/
    ArturRios.IdentityManager.Domain.Tests/
  Infrastructure/
    ArturRios.IdentityManager.Data.Tests/        →  Seeding/MasterUserOptionsTests.cs
  Presentation/
    ArturRios.IdentityManager.WebApi.Tests/      →  ScopeControllerCreateTests.cs (functional)
```

Rules:

- **One test project per production project**, `<ProjectName>.Tests`.
- **One test class per production class under test**, `<ClassName>Tests`, in a namespace that mirrors
  the production namespace.
- The test project **must reference the production project it tests** via `<ProjectReference>`
  (a functional test project references the Web API project; a unit test project references the
  Application/Domain project it covers).
- Test projects set `<IsPackable>false</IsPackable>` and are added to the solution under the matching
  `Tests` solution folder.

## 5. Tooling & packages

Every test project uses the same stack: **xUnit** as the test framework, `coverlet.collector` for coverage, **`ArturRios.Util.Test`** for shared helpers & test doubles, **Moq** for mocking, **Bogus** for test-data generation, and **Testcontainers** (`Testcontainers.PostgreSql`) for the functional database.

> The canonical list of these testing technologies **and their pinned versions** lives in the [Technology Stack Document](Technology%20Stack%20Document.md) §7. This section covers *how* to use them; see that document for *what* (packages and versions).

From `ArturRios.Util.Test`, always prefer the provided building blocks instead of rolling your own:

- **Test attributes** (they stamp a `Category` trait so suites can be filtered):
  - `[UnitFact]` / `[UnitTheory]` → `Category=Unit`
  - `[FunctionalFact]` / `[FunctionalTheory]` → `Category=Functional`
  - Each accepts an optional list of `EnvironmentType`s in which the test must **not** run, and an
    optional skip condition.
- **`WebApiTest<TEntryPoint>`** — base class for functional tests. Spins up an in-memory host via
  `WebApplicationFactory<T>`, and exposes:
  - `Gateway` — an `HttpGateway` for issuing HTTP requests.
  - `AuthenticateAsync`, `Authorize`, `AuthenticateAndAuthorizeAsync` — for authenticated calls.
- **`FakeRepository<T>`** — in-memory repository test double for unit tests (seed it in *Given*).
- **`FakeScheduler`** — simulates delayed command/query dispatch.
- **`CustomAssert`** — extra assertions (`NullOrEmpty`, `NotNullOrEmpty`, `NullOrWhiteSpace`, …).

Beyond the `ArturRios.Util.Test` helpers:

- **Moq** is the **standard mocking library** — use it for every mock/stub of a collaborator that
  isn't already covered by a purpose-built fake from `ArturRios.Util.Test`. Do not introduce a second
  mocking framework.
- **Bogus** is the **standard way to generate test data** — use it whenever a test needs populated
  entities, commands, or DTOs, instead of hand-writing large object literals or relying on shared
  mutable fixtures.

> Use `dotnet test --filter "Category=Unit"` or `"Category=Functional"` to run one kind at a time.

## 6. Unit testing standard (Commands, Queries, Domain behavior)

### 6.1 Scope of a unit test

A unit test instantiates the class under test **directly**, passing test doubles for every
constructor dependency, invokes one method, and asserts the outcome. No web host, no database, no
network, no real time.

Handlers in this project return an `ArturRios.Output.DataOutput<T>` and report failures **as errors
on that output** rather than by throwing (see
[`CreateScopeCommandHandler`](../src/Application/ArturRios.IdentityManager.Command/Handlers/CreateScopeCommandHandler.cs)).
Unit tests therefore assert on `output.Success`, `output.Errors`, `output.Messages`, and
`output.Data` — not on exceptions (except where the code is genuinely expected to throw).

### 6.2 Test doubles

- Replace **repository** dependencies (`IAsyncReadOnlyRepository<T>`, `IAsyncRepository<T>`) with
  `FakeRepository<T>` from `ArturRios.Util.Test`, seeded in the *Given* step to represent the
  database state the scenario assumes.
- Replace **other collaborators** (validators, mediators, e-mail/token/clock services) with a **Moq**
  mock configured to return the value the scenario needs (`new Mock<IValidator<T>>()`,
  `mock.Setup(...).ReturnsAsync(...)`, and `mock.Object` as the dependency). Moq is the single
  mocking library for the project — don't hand-roll one-off fakes when a purpose-built fake from
  `ArturRios.Util.Test` doesn't already exist, and don't mix in another mocking framework.
- Generate any **entities, commands, or DTOs** a scenario needs with **Bogus** (`Faker<T>`), rather
  than large inline object literals or shared mutable fixtures. Set only the fields the behavior under
  test actually depends on; let Bogus fill the rest so tests stay focused and independent.
- The goal is always the same: pin every collaborator to a known result so the only variable is the
  logic of the method under test.

### 6.3 Structure & naming

- Test method name: **`GivenSomeCondition_WhenSomeAction_ThenSomeOutput`**.
- Method body is split into three commented sections in order: `// Given`, `// When`, `// Then`.
- One logical behavior per test. Use `[UnitTheory]` with `[InlineData]`/`[MemberData]` for the same
  behavior across multiple inputs.

### 6.4 Coverage per handler

For each Command/Query handler, cover:

1. The **main (success) flow** of the use case.
2. **Every alternative/exception flow** defined for that use case (the `AF-xx` rows in the Use Case
   Specification). Authorization flows enforced by the framework/middleware (e.g. `403 Forbidden`
   via `[RoleRequirement]`) are verified in the **functional** tests instead, not here.

Example — UC-01 (Create Scope) has main flow + `AF-01a` (name exists), `AF-01b` (invalid input /
no owner), `AF-01d` (owner is not a valid `ScopeAdmin`). `AF-01c` (not System Admin) is a functional
concern. That yields unit tests such as:

```csharp
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Bogus;
using FluentValidation;
using FluentValidation.Results;
using Moq;

namespace ArturRios.IdentityManager.Command.Tests;

public class CreateScopeCommandHandlerTests
{
    [UnitFact]
    public async Task GivenUniqueNameAndValidOwners_WhenHandlingCreateScope_ThenScopeIsCreated()
    {
        // Given
        var scopeReader = new FakeRepository<Scope>();
        var scopeWriter = scopeReader;                     // same in-memory store
        var roleReader = new FakeRepository<Role>();
        var scopeAdminRole = new Role { Name = nameof(Roles.ScopeAdmin) };
        roleReader.Create(scopeAdminRole);

        // Bogus builds the collaborator data; set only the fields the behavior depends on.
        var owner = new Faker<Person>()
            .RuleFor(p => p.PublicId, _ => Guid.NewGuid())
            .RuleFor(p => p.RoleId, _ => scopeAdminRole.Id)
            .RuleFor(p => p.IsDeleted, _ => false)
            .Generate();
        var personReader = new FakeRepository<Person>();
        personReader.Create(owner);

        // Moq stubs the non-repository collaborator (the validator).
        var validator = new Mock<IValidator<CreateScopeCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<CreateScopeCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());          // no failures = valid

        var handler = new CreateScopeCommandHandler(
            validator.Object, scopeReader, personReader, roleReader, scopeWriter);

        var command = new CreateScopeCommand { Name = "Acme", OwnerIds = [owner.PublicId] };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.Equal("Acme", output.Data!.Name);
        Assert.Contains(ScopeMessages.ScopeCreatedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenScopeNameAlreadyExists_WhenHandlingCreateScope_ThenReturnsNameAlreadyExistsError()
    {
        // Given a store already containing a scope named "Acme" …
        // When handling a CreateScopeCommand for "Acme" …
        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.NameAlreadyExists, output.Errors);
    }

    // GivenInvalidInput_… (AF-01b), GivenOwnerIsNotScopeAdmin_… (AF-01d), etc.
}
```

> The example shows the **pattern**: construct the handler with fakes, drive one behavior, assert on
> `DataOutput`. Adapt the fakes to the collaborators of the handler you are testing.

### 6.5 Domain behavior tests

When a Domain class gains real behavior, unit-test that behavior in `*.Domain.Tests`, same GWT
pattern, no infrastructure. Test the method's contract: valid inputs produce the expected result;
invalid inputs are rejected/guarded; boundaries and invariants hold.

## 7. Functional testing standard (Web API)

### 7.1 Scope of a functional test

A functional test is an **end-to-end** test of a use case through the API. It:

1. Starts the Web API host (via `WebApiTest<Program>`).
2. Points the app at a **real PostgreSQL database** running in a throwaway Testcontainers container.
3. Issues real HTTP requests through `Gateway`.
4. Asserts **both**:
   - the **API response** (HTTP status code, body/`DataOutput` payload, messages/errors), **and**
   - the **database state** after the operation (row created/updated/deleted, join rows present,
     `IsDeleted` toggled, etc.).

This is where authorization, routing, model binding, validation wiring, the mediator, EF Core
mapping, and the database schema are all verified together. The existing
[`HealthCheckTests`](../tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/HealthCheckTests.cs)
is the reference for the request/response half of this pattern:

```csharp
public class HealthCheckTests(EnvironmentType environment = EnvironmentType.Local)
    : WebApiTest<Program>(environment)
{
    [FunctionalFact]
    public async Task GivenApiWorking_WhenHealthCheckEndpointCalled_ThenEndpointReturnsOk()
    {
        var output = await Gateway.GetAsync<DataOutput<string>>("/HealthCheck");

        Assert.Equal(HttpStatusCode.OK, output.StatusCode);
        Assert.Equal("Hello world!", output.Body?.Data);
    }
}
```

### 7.2 The database: Testcontainers PostgreSQL

Functional tests **must not** use an in-memory EF provider or a developer's local database. They run
against a real PostgreSQL instance created by Testcontainers at test time, so the environment closely
matches production (the app uses the PostgreSQL provider — see
[`Startup.AddDependencies`](../src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs)).

The app reads its connection from environment variables (`IDENTITY_MANAGER_DATA_CONNECTIONSTRING`,
`IDENTITY_MANAGER_DATA_DATABASETYPE`) via `AddDataConfigFromEnvironment<AppDbContext>`. The functional
suite therefore:

1. Starts a `PostgreSqlContainer` **once for the whole functional suite** (an xUnit
   `ICollectionFixture`, so the container isn't recreated per test class).
2. Applies the schema/migrations to that container's database.
3. Exports the container's connection string into `IDENTITY_MANAGER_DATA_CONNECTIONSTRING` (and
   `IDENTITY_MANAGER_DATA_DATABASETYPE=PostgreSql`) **before** the host is built, so
   `WebApiTest<Program>` boots against the container.
4. **Resets state between tests** (truncate tables or run each test in a rolled-back transaction) so
   tests remain independent and order-agnostic.

This shared container/fixture is built **once** as test infrastructure; individual use-case tests
just derive from the functional base class and use it.

```csharp
// Shared once for the whole functional suite.
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Environment.SetEnvironmentVariable("IDENTITY_MANAGER_DATA_CONNECTIONSTRING", ConnectionString);
        Environment.SetEnvironmentVariable("IDENTITY_MANAGER_DATA_DATABASETYPE", "PostgreSql");
        // Create the schema by applying the real EF Core migrations — see §10.1.
        await using var context = new AppDbContext(/* options bound to ConnectionString */);
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(nameof(FunctionalCollection))]
public sealed class FunctionalCollection : ICollectionFixture<PostgresFixture>;
```

### 7.3 Structure, naming, and assertions

- Functional test classes derive from `WebApiTest<Program>`, join the functional collection, and use
  `[FunctionalFact]` / `[FunctionalTheory]`.
- Same GWT naming and `// Given / // When / // Then` sections as unit tests.
- Authenticate with `AuthenticateAndAuthorizeAsync` when the endpoint requires a role, and add
  explicit tests for the authorization alternative flows (`403 Forbidden`, `401 Unauthorized`).
- In *Then*, assert the response **and** open the database (through the fixture / a scoped
  `AppDbContext`) to assert the persisted state.

```csharp
[Collection(nameof(FunctionalCollection))]
public class ScopeControllerTests : WebApiTest<Program>
{
    public ScopeControllerTests(PostgresFixture db) : base(EnvironmentType.Local) { /* seed as needed */ }

    [FunctionalFact]
    public async Task GivenSystemAdminAndValidPayload_WhenPostScopes_ThenScopeIsCreatedAndReturned()
    {
        // Given an authenticated System Admin and an existing ScopeAdmin owner …
        // When
        var response = await Gateway.PostAsync<DataOutput<CreateScopeCommandOutput>>(
            "/api/scopes", new CreateScopeCommand { Name = "Acme", OwnerIds = [ownerPublicId] });

        // Then — response
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Acme", response.Body?.Data?.Name);

        // Then — database state
        // load the Scope + its SCOPE_OWNER rows from the DB and assert they exist as expected
    }

    // GivenDuplicateName_… → 409 (AF-01a)
    // GivenInvalidPayload_… → 400 (AF-01b)
    // GivenNonSystemAdmin_… → 403 (AF-01c)
    // GivenOwnerNotScopeAdmin_… → 400 (AF-01d)
}
```

### 7.4 Coverage per endpoint

For each controller endpoint of the use case, cover the **main flow** and **every alternative flow**
from the Use Case Specification, including the authorization flows that unit tests deliberately skip.

## 8. Naming & style conventions (quick reference)

| Item | Convention | Example |
| --- | --- | --- |
| Test project | `<Project>.Tests` | `ArturRios.IdentityManager.Command.Tests` |
| Test class | `<ClassUnderTest>Tests` | `CreateScopeCommandHandlerTests` |
| Test method | `Given…_When…_Then…` | `GivenScopeNameAlreadyExists_WhenHandlingCreateScope_ThenReturnsNameAlreadyExistsError` |
| Unit fact/theory | `[UnitFact]` / `[UnitTheory]` | — |
| Functional fact/theory | `[FunctionalFact]` / `[FunctionalTheory]` | — |
| Body sections | `// Given` → `// When` → `// Then` | — |

## 9. Per-use-case workflow (apply every time)

After implementing a use case, do the following before considering it complete:

1. **Identify the artifacts** the use case produced (handlers, validators, domain behavior, endpoints)
   using the table in §3.
2. **Create/locate the test projects** that mirror those artifacts' projects (§4), each referencing
   the project under test, with the standard package set (§5).
3. **Unit-test each Command/Query handler**: main flow + every applicable `AF-xx` alternative flow,
   with all collaborators faked/mocked (§6).
4. **Unit-test any new Domain behavior** (§6.5). Skip anemic entities.
5. **Functional-test each endpoint** end-to-end against the Testcontainers PostgreSQL database,
   asserting both response and database state, covering main flow + every `AF-xx`, including
   authorization flows (§7).
6. **Name everything** per §8 and write every test in Given/When/Then form.
7. **Run the suite** (`dotnet test`) and confirm both `Category=Unit` and `Category=Functional` pass.
8. Only then is the use case done.

## 10. Current test inventory

Every test project uses the standard package set from §5, references the project it tests, and is
registered in the solution under the `Tests` folder. Six projects mirror the `src/` tree:

| Test project | References | Test classes |
| --- | --- | --- |
| `tests/Application/ArturRios.IdentityManager.Command.Tests` | `…Command` | Handler tests for every command — `CreateScope`, `UpdateScope`, `DeleteScope`, `HardDeleteScope`, `CreateAdmin`, `CreateUser`, `CreateScopeOwner`, `UpdatePerson`, `DeletePerson`, `HardDeletePerson`, `Login` — plus the validators that guard them and `EmailVerificationServiceTests` |
| `tests/Application/ArturRios.IdentityManager.Query.Tests` | `…Query` | `GetScopeByIdQueryHandlerTests`, `ListScopesQueryHandlerTests`, `GetPersonByIdQueryHandlerTests`, `ListScopePersonsQueryHandlerTests`, `ListScopeOwnersQueryHandlerTests`, `GetDetailedHealthQueryHandlerTests`, `DatabaseHealthCheckTests` |
| `tests/Application/ArturRios.IdentityManager.Shared.Tests` | `…Shared` | `ScopeOwnershipCheckerTests` — the scope-authorization rule shared by UC-06 AF-06e and UC-07 AF-07b |
| `tests/Domain/ArturRios.IdentityManager.Domain.Tests` | `…Domain` | Empty by design — every entity is still anemic, so §3's rule gives them no unit tests |
| `tests/Infrastructure/ArturRios.IdentityManager.Data.Tests` | `…Data` | `Seeding/MasterUserOptionsTests` |
| `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests` | `…WebApi` | One functional class per endpoint group (`ScopeController*`, `PersonController*`, `AuthControllerLogin`, `HealthCheck`), plus `SchemaTests`, `SeedingTests`, the unit-tested `IdentityUserMapperTests`, and `Support/` (`PostgresFixture`, `FunctionalCollection`, `TestTokens`) |

Suite totals as of UC-11: **179 unit** and **136 functional** tests, all passing. Run them
separately with `--filter "Category=Unit"` / `"Category=Functional"` (see the README).

### 10.1 Functional database

`PostgresFixture` starts a `postgres:16-alpine` container, points
`IDENTITY_MANAGER_DATA_CONNECTIONSTRING` / `…_DATABASETYPE` at it before the host is built — so the
suite never touches a developer's local `.env.local` database — and creates the schema by applying
**the real EF Core migrations** (`context.Database.MigrateAsync()`). Migrations were chosen over
`EnsureCreated` deliberately: the API refuses to start with migrations pending (see
`DatabaseSeeder`), and applying them here means the functional suite exercises the same schema
production gets. `SchemaTests` asserts the result — every table under the `identity_manager` schema,
named in singular snake_case.

`SeedingTests` covers the other half of startup: `DatabaseSeeder` writes the three `Roles` rows and
the master system administrator, so functional tests can authenticate as a System Admin.
