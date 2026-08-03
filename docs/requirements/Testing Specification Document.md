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

### 5.1 Every run records a `.trx`

`tests/Directory.Build.props` points every test project at `tests/default.runsettings`, which enables
the `trx` logger. An ordinary `dotnet test` therefore writes a result file per project under
`TestResults/` (git-ignored) and prints its path.

This exists because a console summary is not evidence. A functional test failed once in a
full-solution run, the summary was the only record, and the failing test's name was lost — the
failure never reproduced in 23 subsequent runs, so there was nothing left to investigate. A `.trx`
costs nothing per run and means the next intermittent failure identifies itself.

To chase a failure that only appears occasionally, repeat the run and let the harness collect the
names:

```bash
python scripts/flake_hunt.py --runs 25
```

It runs the whole solution in a loop, stops at the first failing run (`--keep-going` measures a rate
instead), and prints every test any run recorded as failed. Hunt with the **whole solution** rather
than a single project: a failure that only appears under the CPU contention of parallel test
projects will not reproduce from one project alone.

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
4. **Keeps tests independent by making their data unique, not by resetting the database.** The
   container's database accumulates rows for the whole run; nothing is truncated between tests and
   no test runs in a rolled-back transaction. Independence comes from every test seeding its own
   rows under identifiers no other test can produce — `Guid.NewGuid()` public identifiers,
   `scope-{Guid:N}` names, `user-{Guid:N}@test.local` addresses — and asserting only about those
   rows.

   This is a deliberate choice, not an omission. Truncation would delete the roles and the master
   system administrator that `DatabaseSeeder` writes at startup, which `SeedingTests` and the login
   tests legitimately depend on, and a per-test transaction cannot wrap work done by the API's own
   `DbContext` inside the host.

   **What this asks of a new test:** never assert on a global count or an unfiltered query — the
   only two in the suite (`Roles == 3`, and the master user counted by e-mail) are safe because
   nothing else can create those rows. Filter every listing by something unique to the test, the way
   `ScopeControllerViewTests` filters by the scope name it just generated. A test that counts *all*
   scopes passes alone and fails the moment another class seeds one.

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
| `tests/Application/ArturRios.IdentityManager.Command.Tests` | `…Command` | Handler tests for every command — `CreateScope`, `UpdateScope`, `DeleteScope`, `HardDeleteScope`, `CreateAdmin`, `CreateUser`, `CreateScopeOwner`, `AddScopeOwner`, `RemoveScopeOwner`, `PromoteScopeUser`, `SetGoogleSignIn`, `GoogleSignIn`, `GoogleSignOut`, `DeleteGoogleUser`, `HardDeleteGoogleUser`, `UpdatePerson`, `DeletePerson`, `HardDeletePerson`, `Login`, `PasswordRecovery`, `ResetPassword`, `VerifyEmail`, `ResendVerificationEmail`, `CreateApplication`, `UpdateApplication`, `DeleteApplication`, `HardDeleteApplication` — plus the validators that guard them, `EmailVerificationServiceTests`, and `PasswordResetServiceTests` |
| `tests/Application/ArturRios.IdentityManager.Query.Tests` | `…Query` | `GetScopeByIdQueryHandlerTests`, `ListScopesQueryHandlerTests`, `GetPersonByIdQueryHandlerTests`, `ListScopePersonsQueryHandlerTests`, `ListScopeOwnersQueryHandlerTests`, `GetApplicationByIdQueryHandlerTests`, `ListScopeApplicationsQueryHandlerTests`, `GetGoogleUserByIdQueryHandlerTests`, `ListScopeGoogleUsersQueryHandlerTests`, `GetDetailedHealthQueryHandlerTests`, `DatabaseHealthCheckTests` |
| `tests/Application/ArturRios.IdentityManager.Shared.Tests` | `…Shared` | `ScopeOwnershipCheckerTests` — the scope-authorization rule shared by UC-06 AF-06e and UC-07 AF-07b |
| `tests/Domain/ArturRios.IdentityManager.Domain.Tests` | `…Domain` | Empty by design — every entity is still anemic, so §3's rule gives them no unit tests |
| `tests/Infrastructure/ArturRios.IdentityManager.Data.Tests` | `…Data` | `Seeding/MasterUserOptionsTests` |
| `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests` | `…WebApi` | One functional class per endpoint group (`ScopeController*`, `PersonController*`, `AuthControllerLogin`, `AuthControllerPasswordRecovery`, `AuthControllerPasswordReset`,
`AuthControllerVerifyEmail`, `AuthControllerResendVerification`, `AuthControllerGoogleSignIn`, `AuthControllerGoogleSignOut`, `GoogleUserControllerView`, `GoogleUserControllerDelete`, `GoogleUserControllerHardDelete`, `ApplicationController*`, `HealthCheck`), plus `SchemaTests`, `SeedingTests`, the unit-tested `IdentityUserMapperTests` and `MailgunSenderTests`, and `Support/` (`PostgresFixture`, `FunctionalCollection`, `TestTokens`, `TestGoogleTokens`) |

Suite totals as of UC-29: **445 unit** and **364 functional** tests, all passing. Run them separately
with `--filter "Category=Unit"` / `"Category=Functional"` (see the README).

UC-18 added `UpdateApplicationCommandHandlerTests` and `UpdateApplicationCommandValidatorTests` to
the Command.Tests project, and `ApplicationControllerUpdateTests` to the functional suite.

UC-25 added `GoogleSignInCommandHandlerTests` and `AuthControllerGoogleSignInTests`, 14 tests each,
covering both halves of the main flow and AF-25a…AF-25d. Its functional half also added
`Support/TestGoogleTokens`, and is worth reading before writing tests for UC-26…UC-29, because it
solves a problem those use cases inherit: **how to test an endpoint whose input is verified by a
third party.**

`WebApiTest<T>` keeps its `WebApplicationFactory` private and its `Gateway` protected-readonly, so a
functional test cannot replace a DI registration — every substitution in this suite is chosen at
start-up from the environment, as `Startup.AddEmailSenders` is. `PostgresFixture` therefore publishes
`IDENTITY_MANAGER_GOOGLE_TEST_SIGNING_SECRET`, which makes the host under test resolve
`LocalGoogleIdTokenVerifier`, and `TestGoogleTokens` mints ID tokens signed with the same secret.
Without it only AF-25a and AF-25b would be reachable: the main flow, AF-25c, and AF-25d all sit
behind token verification, and §7.4 asks for every alternative flow.

UC-26 added `GoogleSignOutCommandHandlerTests` (4) and `AuthControllerGoogleSignOutTests` (8),
covering the main flow and all four shapes AF-26a takes. It is the suite's first endpoint whose
success writes nothing, so one test on each side asserts the Google User row is **unchanged**
afterwards — without it, a handler that deleted the account instead of ending its session would pass
every other assertion.

Its functional half also consumes what UC-25 built: one test signs in through `POST /api/auth/google`
with a `TestGoogleTokens` ID token and then signs out with the application token that call returned,
which is the only way to prove UC-26's precondition end to end rather than assume it.

UC-27 added `GetGoogleUserByIdQueryHandlerTests` (9), `ListScopeGoogleUsersQueryHandlerTests` (8),
and `GoogleUserControllerViewTests` (19). The functional half deliberately covers **both** endpoints
in one class rather than the usual one-class-per-endpoint split: the thing worth pinning is the
asymmetry between them — a Google User may read their own record but may never list a scope — and
splitting the class would put the two halves of that single rule in two files where neither states
it.

UC-28 added `DeleteGoogleUserCommandHandlerTests` (7) and `GoogleUserControllerDeleteTests` (10).
Two of the functional tests deliberately reach past the endpoint under test, because **a flag
nothing honours is not a deletion**: one confirms UC-27's default read stops returning the record
(FR-GO-17), and one signs an account up through UC-25, deletes it, and confirms the same GoogleId is
refused a fresh sign-in (AF-25d) without a duplicate row appearing to route around the deletion.
Asserting only `IsDeleted == true` would have passed against an implementation nothing downstream
respected.

UC-29 added `HardDeleteGoogleUserCommandHandlerTests` (5) and
`GoogleUserControllerHardDeleteTests` (9), completing the Google Sign-In milestone. The unit half
has **no authorization test**, deliberately: UC-29's only actor is the System Admin, the endpoint's
`RoleRequirement` settles it entirely, and the command carries no acting person — so there is no
rule at that level to test, and the functional half proves the endpoint enforces it instead. That
includes an *owning* Scope Admin being refused, which is the whole difference between UC-28 and
UC-29.

One functional test asserts the scope and its other Google Users survive the deletion. The foreign
key points from the Google User to the scope, so a cascade in the wrong direction would take
everything in it — and would be silent in a suite that only checked the deleted row was gone.

The substitute is deliberately **not** a trust-anything stub — a token still needs a valid HS256
signature, the expected issuer and audience, and an unexpired lifetime, so the suite exercises the
same verification path a real deployment does and only the signing authority differs. Two guards keep
it out of production: `Startup` never registers it in the Production environment, and never without
that variable explicitly set. No `.env` file carries it.

UC-19 added `DeleteApplicationCommandHandlerTests` to the Command.Tests project and
`ApplicationControllerDeleteTests` to the functional suite. It has no validator test class: the
command carries no body, so there is no validator to guard — the same shape `DeleteScopeCommand` and
`DeletePersonCommand` already have.

UC-20 added `HardDeleteApplicationCommandHandlerTests` to the Command.Tests project and
`ApplicationControllerHardDeleteTests` to the functional suite, and likewise has no validator test
class. Its unit tests cover only the main flow and AF-20a: UC-20's single actor means authorization is
settled by `[RoleRequirement]` alone and the command carries no acting person, so every refusal —
including the Scope Admin who owns the application — is a functional test. The pair also pins the two
deletions apart: `ApplicationControllerDeleteTests` asserts a repeated call is an idempotent `200`,
while `ApplicationControllerHardDeleteTests` asserts it is a `404`.

UC-21 added `AddScopeOwnerCommandHandlerTests` to the Command.Tests project and
`PersonControllerAddScopeOwnerTests` to the functional suite, and has no validator test class — both
identifiers are route values, so there is no body to guard. It is the first use case whose main flow
and idempotent alternative flow answer with **different** status codes: AF-21d returns `200` where the
main flow returns `201`, so the functional pair asserts the two statuses *and* that a second call
leaves exactly one `scope_owner` row. Contrast UC-19, where AF-19b shares the main flow's `200` and
only the `AlreadyDeleted` flag separates them (§10.2 covers the harder case, where even the message
matches).

Its unit tests also pin an ordering guarantee rather than a flow: an actor the ownership checker
rejects, naming a person who does not exist, must be refused with AF-21c and never AF-21b — the
authorization answer must not depend on data the caller is not allowed to learn.

UC-22 added `RemoveScopeOwnerCommandHandlerTests` to the Command.Tests project and
`PersonControllerRemoveScopeOwnerTests` to the functional suite, and likewise has no validator test
class. It repeats UC-21's ordering test on the inverse operation and adds two shapes of its own:

- **Every refusal asserts the `scope_owner` row survives.** A removal endpoint that answered the
  right status while still deleting the row would pass a status-only test, so each 403/404/409/401
  case reads the join row back — from the fake repository at the unit layer, from PostgreSQL at the
  functional one.
- **The last-owner guard is tested from both sides of "live".** `GivenSoleOwner_…` covers the plain
  NFR-12 case, and `GivenOnlyCoOwnerIsLogicallyDeleted_…` covers the one that makes the guard's
  `!IsDeleted` filter a tested claim: a deleted co-owner does not keep a scope owned, so removing the
  only live owner is still refused. The mirror case,
  `GivenLogicallyDeletedTargetWithLiveCoOwner_…`, pins that a deleted *target* is nonetheless
  removable — the stale row this endpoint exists to clear.

It also contrasts with UC-21 on repetition a third way: `PersonControllerAddScopeOwnerTests` asserts
a second call is an idempotent `200` and `PersonControllerPromoteScopeUserTests` a `409`, while
`PersonControllerRemoveScopeOwnerTests` asserts a `404` — the row is gone, so the repeat meets
AF-22a rather than finding anything to answer for.

UC-23 added `PromoteScopeUserCommandHandlerTests` to the Command.Tests project and
`PersonControllerPromoteScopeUserTests` to the functional suite, and likewise has no validator test
class. It repeats UC-21's ordering test and adds two of its own kinds:

- **Ordering between two alternative flows, not just between a flow and authorization.** AF-23d
  (already a `ScopeAdmin`, 409) is checked before AF-23b (not a `User` of this scope, 400), because
  every person AF-23d describes also satisfies AF-23b. `GivenPersonAlreadyScopeAdmin_…` would still
  pass against a handler that answered 400, so it asserts the 409 *and* that the ownership rows the
  person already held are untouched.
- **A requirement the use case does not name.** Promotion moves the address from the scope's `User`
  namespace into the system-wide admin one (FR-PE-09), which no `AF-23x` covers. Three tests pin it:
  a live admin holding the address refuses the promotion, a *logically deleted* admin does not, and a
  `User` of another scope holding it does not either — the last one is what makes "the two namespaces
  are independent" a tested claim rather than a comment.

The pair also contrasts with UC-21 on repetition: `PersonControllerAddScopeOwnerTests` asserts a
second call is an idempotent `200`, while `PersonControllerPromoteScopeUserTests` asserts it is a
`409` — the first promotion changed the role, so the repeat meets AF-23d rather than finding nothing
to do.

UC-24 added `SetGoogleSignInCommandHandlerTests` and `SetGoogleSignInCommandValidatorTests` to the
Command.Tests project and `ScopeControllerSetGoogleSignInTests` to the functional suite. It is the
first use case whose command carries a **single** field and still earns a validator test class, and
the reason is the point of the coverage: `Enabled` is a `bool?` because a plain `bool` would bind a
body that omits the field to `false` and silently *disable* Google Sign-In (AF-24c). Three tests pin that —
the validator rejecting `null`, the handler refusing the validation failure, and
`GivenEmptyBody_WhenPutGoogleSignIn_ThenBadRequestAndFlagIsUnchanged`, which sends `{}` against an
*enabled* scope so a regression shows up as a flipped row rather than only a changed status code.

Two smaller shapes it contributes:

- **A toggle is tested in both directions.** The main flow is not one test but two — enable and
  disable — at both layers, because "Enable/Disable" is one endpoint doing opposite things and a
  handler that ignored the requested value would still pass the enable-only half.
- **Every refusal asserts the persisted flag, not just the status.** AF-24b's whole content is that
  an unauthorized actor changes nothing, so each 403/404/400/401 test reads
  `google_sign_in_enabled` back from the database. The functional helper sends the SRD's wire body
  (`{ enabled }`) as an anonymous object rather than a serialized command, so the test pins the
  contract a client uses.

UC-17 also carried a specification correction — application ownership was restricted to a
`ScopeAdmin` who owns the application's scope — so the UC-16 tests were rewritten rather than merely
added to. `CreateApplicationCommandHandlerTests` lost its `User`-actor and `SCOPE_USER`-owner cases
and gained the Scope Admin equivalents; `ApplicationControllerCreateTests` gained the `User`-role
`403` and the `User`-as-owner `400`. A use-case correction is expected to update the tests that
pinned the old behaviour, in the same change.

### 10.2 Testing an endpoint that answers the same way twice

UC-12 is the first use case whose alternative flow is *indistinguishable from its main flow* by
design — AF-12a returns the same 200 and the same message as a successful request, so response
assertions alone cannot tell them apart. Its tests handle that in two ways, and later
anti-enumeration work should follow the same shape:

- **Assert the side effect, not the response.** Every functional test opens the database and asserts
  whether a `password_reset_token` row exists for the person. That row is the only difference
  between the two paths, so it is the only thing worth asserting.
- **Compare the two responses directly.** One test issues both requests and asserts the status,
  messages, and errors are equal. Each other test pins its own path; this one pins the property
  AF-12a actually states.

The senders are unit-tested with a stub `IEmailService` rather than reached over the network. The
functional suite leaves the Mailgun variables unset, so the API registers its logging senders and
never makes an outbound call.

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
