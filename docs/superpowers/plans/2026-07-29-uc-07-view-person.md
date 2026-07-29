# UC-07 View Person Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement UC-07 (View Person) — read a person by id, list a scope's `User` persons, and list a scope's `ScopeAdmin` owners — each enforcing the use case's per-actor visibility rules and excluding logically deleted persons unless explicitly requested.

**Architecture:** CQRS read flow mirroring UC-02 (View Scope). Three queries, three handlers, one shared `PersonOutput`; the existing `PersonController` gains three GET actions. Handlers return `DataOutput<T>` / `PaginatedOutput<T>` and report failures as errors, never exceptions. Scope-scoped authorization reuses `IScopeOwnershipChecker`, which moves from the `Command` project to `Shared` so the read side can consume it without depending on the write side.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core (PostgreSQL), ArturRios.Mediator 1.0.3 / .Output 3.1.0 / .Data.Relational.Core 3.0.2 / .Util.WebApi; xUnit 2.9.3 + Moq 4.20.72 + Bogus 35.6.3 + Testcontainers for tests.

## Global Constraints

- **Design of record:** `docs/superpowers/specs/2026-07-29-uc-07-view-person-design.md`. Every decision below traces to it.
- **No schema change / no EF migration** — `person`, `scope_user`, `scope_owner` and their maps already exist from `InitialCreate`.
- **Identifiers:** routes, inputs and outputs use `PublicId` (GUID); joins and FKs use internal `Id` (bigint). Never expose or accept an internal `Id` (NFR-15). Never return `PasswordHash` / `Salt`.
- **Handlers return `DataOutput<T>` / `PaginatedOutput<T>` and never throw.** Failures are errors carrying a canonical `PersonMessages` value, which `ResponseResolver` maps to a status through `PersonMessageMap.StatusCodes`.
- **Roles:** `SystemAdmin = 1`, `ScopeAdmin = 2`, `User = 3`; the seeder guarantees `Role.Id == (long)Roles`.
- **Acting user:** `AuthenticationMiddleware` attaches an `ArturRios.Util.WebApi.Security.Records.AuthenticatedUser(int Id, int Role)` to `HttpContext.Items["User"]`; the `Id` claim is the person's **internal** `Id`.
- **Tests:** unit tests use `AsyncFakeRepository<T>` from `ArturRios.Util.Test.Mock` and Moq for non-repository collaborators; functional tests derive from `WebApiTest<Program>`, join `[Collection(nameof(FunctionalCollection))]`, authorize via `TestTokens`, and assert response **and** database state via `db.CreateContext()`. GWT naming `Given…_When…_Then…`, `// Given` / `// When` / `// Then` sections, `[UnitFact]` / `[FunctionalFact]`.
- **Run filters:** `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"` and `--filter "Category=Functional"`.
- **Commit style:** lowercase Conventional Commits subject, ≤50 chars, imperative; body wrapped at 72; trailer `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.

---

## File Structure

**New — production:**
- `src/Application/ArturRios.IdentityManager.Shared/Security/IActorScoped.cs` — the two acting-caller members, shared by commands and queries.
- `src/Application/ArturRios.IdentityManager.Shared/Services/IScopeOwnershipChecker.cs` — moved from `Command/Services`.
- `src/Application/ArturRios.IdentityManager.Shared/Services/ScopeOwnershipChecker.cs` — moved from `Command/Services`.
- `src/Application/ArturRios.IdentityManager.Query/Output/PersonOutput.cs` — the person projection returned by all three endpoints.
- `src/Application/ArturRios.IdentityManager.Query/Input/GetPersonByIdQuery.cs`
- `src/Application/ArturRios.IdentityManager.Query/Input/ListScopePersonsQuery.cs`
- `src/Application/ArturRios.IdentityManager.Query/Input/ListScopeOwnersQuery.cs`
- `src/Application/ArturRios.IdentityManager.Query/Handlers/GetPersonByIdQueryHandler.cs`
- `src/Application/ArturRios.IdentityManager.Query/Handlers/ListScopePersonsQueryHandler.cs`
- `src/Application/ArturRios.IdentityManager.Query/Handlers/ListScopeOwnersQueryHandler.cs`

**Deleted — production:**
- `src/Application/ArturRios.IdentityManager.Command/Services/IScopeOwnershipChecker.cs`
- `src/Application/ArturRios.IdentityManager.Command/Services/ScopeOwnershipChecker.cs`
- `src/Application/ArturRios.IdentityManager.Command/Input/IActorScopedCommand.cs`

**Modified — production:**
- `src/Application/ArturRios.IdentityManager.Shared/ArturRios.IdentityManager.Shared.csproj` — add `Domain` project reference and the `ArturRios.Data.Relational.Core` package.
- `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessages.cs` — four new messages.
- `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessageMap.cs` — their status codes.
- `src/Application/ArturRios.IdentityManager.Command/Input/CreateUserCommand.cs`, `CreateScopeOwnerCommand.cs` — implement `IActorScoped`.
- `src/Application/ArturRios.IdentityManager.Command/Handlers/CreateUserCommandHandler.cs`, `CreateScopeOwnerCommandHandler.cs` — `using` for the relocated checker.
- `src/Presentation/ArturRios.IdentityManager.WebApi/Controllers/PersonController.cs` — three GET actions, `ApplyActor(IActorScoped)`.
- `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs` — three handler registrations, relocated checker namespace.
- `src/ArturRios.IdentityManager.sln` — register the new `Shared.Tests` project.

**New — tests:**
- `tests/Application/ArturRios.IdentityManager.Shared.Tests/ArturRios.IdentityManager.Shared.Tests.csproj`
- `tests/Application/ArturRios.IdentityManager.Shared.Tests/ScopeOwnershipCheckerTests.cs` — moved from `Command.Tests`.
- `tests/Application/ArturRios.IdentityManager.Query.Tests/GetPersonByIdQueryHandlerTests.cs`
- `tests/Application/ArturRios.IdentityManager.Query.Tests/ListScopePersonsQueryHandlerTests.cs`
- `tests/Application/ArturRios.IdentityManager.Query.Tests/ListScopeOwnersQueryHandlerTests.cs`
- `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerGetByIdTests.cs`
- `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerListScopePersonsTests.cs`
- `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerListScopeOwnersTests.cs`

**Modified — tests:**
- `tests/Application/ArturRios.IdentityManager.Command.Tests/CreateUserCommandHandlerTests.cs`, `CreateScopeOwnerCommandHandlerTests.cs` — `using` for the relocated checker.
- `tests/Application/ArturRios.IdentityManager.Query.Tests/ArturRios.IdentityManager.Query.Tests.csproj` — add Moq and Bogus.

**Deleted — tests:**
- `tests/Application/ArturRios.IdentityManager.Command.Tests/ScopeOwnershipCheckerTests.cs`

---

## Task 1: Move the scope-ownership checker into `Shared`

Pure relocation. Behaviour must not change; the existing suite is the proof.

**Files:**
- Create: `src/Application/ArturRios.IdentityManager.Shared/Services/IScopeOwnershipChecker.cs`
- Create: `src/Application/ArturRios.IdentityManager.Shared/Services/ScopeOwnershipChecker.cs`
- Delete: `src/Application/ArturRios.IdentityManager.Command/Services/IScopeOwnershipChecker.cs`
- Delete: `src/Application/ArturRios.IdentityManager.Command/Services/ScopeOwnershipChecker.cs`
- Modify: `src/Application/ArturRios.IdentityManager.Shared/ArturRios.IdentityManager.Shared.csproj`
- Modify: `src/Application/ArturRios.IdentityManager.Command/Handlers/CreateUserCommandHandler.cs`
- Modify: `src/Application/ArturRios.IdentityManager.Command/Handlers/CreateScopeOwnerCommandHandler.cs`
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs`
- Modify: `src/ArturRios.IdentityManager.sln`
- Create: `tests/Application/ArturRios.IdentityManager.Shared.Tests/ArturRios.IdentityManager.Shared.Tests.csproj`
- Create: `tests/Application/ArturRios.IdentityManager.Shared.Tests/ScopeOwnershipCheckerTests.cs`
- Modify: `tests/Application/ArturRios.IdentityManager.Command.Tests/CreateUserCommandHandlerTests.cs`
- Modify: `tests/Application/ArturRios.IdentityManager.Command.Tests/CreateScopeOwnerCommandHandlerTests.cs`
- Delete: `tests/Application/ArturRios.IdentityManager.Command.Tests/ScopeOwnershipCheckerTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ArturRios.IdentityManager.Shared.Services.IScopeOwnershipChecker` with
  `Task<bool> ActorMayManageScopeAsync(int actingRole, long actingPersonId, long scopeId)`, and its
  implementation `ScopeOwnershipChecker(IAsyncReadOnlyRepository<Person> personReader)`.

- [ ] **Step 1: Give `Shared` access to the domain and repository abstractions**

Replace the `ItemGroup`s in `src/Application/ArturRios.IdentityManager.Shared/ArturRios.IdentityManager.Shared.csproj` so the file's item groups read:

```xml
    <ItemGroup>
        <PackageReference Include="ArturRios.Util" Version="1.4.2" />
        <PackageReference Include="ArturRios.Data.Relational.Core" Version="3.0.2" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\..\Domain\ArturRios.IdentityManager.Domain\ArturRios.IdentityManager.Domain.csproj" />
    </ItemGroup>
```

Leave the existing `PropertyGroup` untouched.

- [ ] **Step 2: Create the interface in `Shared`**

Create `src/Application/ArturRios.IdentityManager.Shared/Services/IScopeOwnershipChecker.cs`:

```csharp
namespace ArturRios.IdentityManager.Shared.Services;

/// <summary>
///     Decides whether an acting caller is authorized to manage or read a given scope (UC-06 AF-06e,
///     UC-07 AF-07b, and any other scope-scoped authorization): a System Admin always may; any other
///     actor must own the scope (a <c>SCOPE_OWNER</c> row links their person id to it).
/// </summary>
public interface IScopeOwnershipChecker
{
    /// <param name="actingRole">The acting caller's role value (see <c>Roles</c>).</param>
    /// <param name="actingPersonId">The acting caller's internal person id.</param>
    /// <param name="scopeId">The target scope's internal id.</param>
    /// <returns><c>true</c> when the actor is a System Admin or owns the scope; otherwise <c>false</c>.</returns>
    Task<bool> ActorMayManageScopeAsync(int actingRole, long actingPersonId, long scopeId);
}
```

- [ ] **Step 3: Create the implementation in `Shared`**

Create `src/Application/ArturRios.IdentityManager.Shared/Services/ScopeOwnershipChecker.cs`:

```csharp
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Shared.Services;

/// <summary>
///     Default <see cref="IScopeOwnershipChecker" />: a System Admin bypasses ownership; any other
///     actor is authorized only when a <c>SCOPE_OWNER</c> row links their person id to the scope.
/// </summary>
public class ScopeOwnershipChecker(IAsyncReadOnlyRepository<Person> personReader) : IScopeOwnershipChecker
{
    public async Task<bool> ActorMayManageScopeAsync(int actingRole, long actingPersonId, long scopeId)
    {
        // A System Admin bypasses the ownership check entirely (no query needed).
        if (actingRole == (int)Roles.SystemAdmin)
        {
            return true;
        }

        // Otherwise the actor must own the scope.
        return await personReader.Query().AnyAsync(person =>
            person.Id == actingPersonId &&
            person.ScopeOwnerships.Any(ownership => ownership.ScopeId == scopeId));
    }
}
```

- [ ] **Step 4: Delete the originals**

```bash
git rm src/Application/ArturRios.IdentityManager.Command/Services/IScopeOwnershipChecker.cs src/Application/ArturRios.IdentityManager.Command/Services/ScopeOwnershipChecker.cs
```

- [ ] **Step 5: Point every consumer and the DI registration at the new namespace**

Four production files and two test files reference the checker. In each, add:

```csharp
using ArturRios.IdentityManager.Shared.Services;
```

- `src/Application/ArturRios.IdentityManager.Command/Handlers/CreateUserCommandHandler.cs`
- `src/Application/ArturRios.IdentityManager.Command/Handlers/CreateScopeOwnerCommandHandler.cs`
- `tests/Application/ArturRios.IdentityManager.Command.Tests/CreateUserCommandHandlerTests.cs`
- `tests/Application/ArturRios.IdentityManager.Command.Tests/CreateScopeOwnerCommandHandlerTests.cs`
- `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs`

Keep the existing `using ArturRios.IdentityManager.Command.Services;` in the two handlers and the
two handler test classes — `IEmailVerificationService` still lives in that namespace, so removing it
breaks the build.

In `Startup.cs` the registration line itself is unchanged:

```csharp
        Builder.Services.AddScoped<IScopeOwnershipChecker, ScopeOwnershipChecker>();
```

- [ ] **Step 6: Create the `Shared.Tests` project**

Create `tests/Application/ArturRios.IdentityManager.Shared.Tests/ArturRios.IdentityManager.Shared.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <IsPackable>false</IsPackable>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="ArturRios.Util.Test" Version="2.2.0" />
        <PackageReference Include="Bogus" Version="35.6.3" />
        <PackageReference Include="Moq" Version="4.20.72" />
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
        <ProjectReference Include="..\..\..\src\Application\ArturRios.IdentityManager.Shared\ArturRios.IdentityManager.Shared.csproj" />
    </ItemGroup>

</Project>
```

- [ ] **Step 7: Move the ownership tests**

Create `tests/Application/ArturRios.IdentityManager.Shared.Tests/ScopeOwnershipCheckerTests.cs` with the
existing test bodies, changing only the namespace and the `using` for the class under test:

```csharp
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.IdentityManager.Shared.Tests;

// Unit tests for ScopeOwnershipChecker (UC-06 AF-06e / UC-07 AF-07b authorization): a System Admin
// always may act; any other actor must own the target scope (a SCOPE_OWNER row links their person
// id to it).
public class ScopeOwnershipCheckerTests
{
    [UnitFact]
    public async Task GivenSystemAdminActor_WhenCheckingScopeManagement_ThenAllowedWithoutOwnership()
    {
        // Given a store with no ownership rows for the actor
        var persons = new AsyncFakeRepository<Person>();
        var checker = new ScopeOwnershipChecker(persons);

        // When a System Admin (any person id, any scope) is checked
        var allowed = await checker.ActorMayManageScopeAsync((int)Roles.SystemAdmin, actingPersonId: 999, scopeId: 1);

        // Then
        Assert.True(allowed);
    }

    [UnitFact]
    public async Task GivenScopeAdminOwningScope_WhenCheckingScopeManagement_ThenAllowed()
    {
        // Given a ScopeAdmin who owns scope 1
        var persons = new AsyncFakeRepository<Person>();
        var actor = new Person
        {
            RoleId = (long)Roles.ScopeAdmin,
            ScopeOwnerships = [new ScopeOwner { ScopeId = 1 }]
        };
        await persons.CreateAsync(actor);
        var checker = new ScopeOwnershipChecker(persons);

        // When
        var allowed = await checker.ActorMayManageScopeAsync((int)Roles.ScopeAdmin, actor.Id, scopeId: 1);

        // Then
        Assert.True(allowed);
    }

    [UnitFact]
    public async Task GivenScopeAdminNotOwningScope_WhenCheckingScopeManagement_ThenNotAllowed()
    {
        // Given a ScopeAdmin who owns no scope
        var persons = new AsyncFakeRepository<Person>();
        var actor = new Person { RoleId = (long)Roles.ScopeAdmin };
        await persons.CreateAsync(actor);
        var checker = new ScopeOwnershipChecker(persons);

        // When checked against scope 1 (which they do not own)
        var allowed = await checker.ActorMayManageScopeAsync((int)Roles.ScopeAdmin, actor.Id, scopeId: 1);

        // Then
        Assert.False(allowed);
    }
}
```

Then remove the old copy:

```bash
git rm tests/Application/ArturRios.IdentityManager.Command.Tests/ScopeOwnershipCheckerTests.cs
```

- [ ] **Step 8: Register the new test project in the solution**

```bash
dotnet sln src/ArturRios.IdentityManager.sln add tests/Application/ArturRios.IdentityManager.Shared.Tests/ArturRios.IdentityManager.Shared.Tests.csproj --solution-folder Tests/Application
```

- [ ] **Step 9: Build and run the unit suite**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"`
Expected: build succeeds and every unit test passes, including the three `ScopeOwnershipCheckerTests` now reported from `ArturRios.IdentityManager.Shared.Tests`.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "refactor: move scope ownership checker to shared"
```

---

## Task 2: Generalize `IActorScopedCommand` into `IActorScoped`

The three UC-07 queries need the same two acting-caller members, so the interface stops being
command-specific. Name and namespace change only.

**Files:**
- Create: `src/Application/ArturRios.IdentityManager.Shared/Security/IActorScoped.cs`
- Delete: `src/Application/ArturRios.IdentityManager.Command/Input/IActorScopedCommand.cs`
- Modify: `src/Application/ArturRios.IdentityManager.Command/Input/CreateUserCommand.cs`
- Modify: `src/Application/ArturRios.IdentityManager.Command/Input/CreateScopeOwnerCommand.cs`
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/Controllers/PersonController.cs`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `ArturRios.IdentityManager.Shared.Security.IActorScoped` with settable
  `long ActingPersonId` and `int ActingRole`.

- [ ] **Step 1: Create the shared interface**

Create `src/Application/ArturRios.IdentityManager.Shared/Security/IActorScoped.cs`:

```csharp
namespace ArturRios.IdentityManager.Shared.Security;

/// <summary>
///     A command or query whose authorization depends on the acting caller. The controller populates
///     these fields from the authenticated user (never from the request) so the handler can enforce
///     scope-scoped rules such as UC-06 AF-06e and UC-07 AF-07b.
/// </summary>
public interface IActorScoped
{
    /// <summary>The acting caller's internal person id.</summary>
    long ActingPersonId { get; set; }

    /// <summary>The acting caller's role value (see <c>Roles</c>).</summary>
    int ActingRole { get; set; }
}
```

- [ ] **Step 2: Delete the command-only interface**

```bash
git rm src/Application/ArturRios.IdentityManager.Command/Input/IActorScopedCommand.cs
```

- [ ] **Step 3: Point the two commands at it**

In `src/Application/ArturRios.IdentityManager.Command/Input/CreateUserCommand.cs`, add the `using`
and change the base list:

```csharp
using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Command;
```

```csharp
public class CreateUserCommand : BaseCommand, IActorScoped
```

Apply the identical two edits to
`src/Application/ArturRios.IdentityManager.Command/Input/CreateScopeOwnerCommand.cs` (its class
declaration becomes `public class CreateScopeOwnerCommand : BaseCommand, IActorScoped`). Leave every
property in both files untouched.

- [ ] **Step 4: Widen the controller helper**

In `src/Presentation/ArturRios.IdentityManager.WebApi/Controllers/PersonController.cs`, add
`using ArturRios.IdentityManager.Shared.Security;` and change the helper's parameter type and
comment:

```csharp
    /// <summary>
    ///     Copies the authenticated caller (attached to the request by the auth middleware) onto an
    ///     actor-scoped command or query, so the handler can enforce scope-scoped authorization
    ///     (UC-06 AF-06e, UC-07 AF-07b). The acting fields are always taken from the token, never
    ///     from the request.
    /// </summary>
    private void ApplyActor(IActorScoped actorScoped)
    {
        var actor = (AuthenticatedUser)HttpContext.Items["User"]!;
        actorScoped.ActingPersonId = actor.Id;
        actorScoped.ActingRole = actor.Role;
    }
```

The three existing call sites (`ApplyActor(command)`) need no change.

- [ ] **Step 5: Build and run the unit suite**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"`
Expected: build succeeds, every unit test still passes.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: generalize actor-scoped command interface"
```

---

## Task 3: `GET /api/persons/{id}` query handler

Delivers the by-id read (FR-PE-03) with its visibility rule, plus the output type and the messages
the two later tasks also use.

**Files:**
- Create: `src/Application/ArturRios.IdentityManager.Query/Output/PersonOutput.cs`
- Create: `src/Application/ArturRios.IdentityManager.Query/Input/GetPersonByIdQuery.cs`
- Create: `src/Application/ArturRios.IdentityManager.Query/Handlers/GetPersonByIdQueryHandler.cs`
- Modify: `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessages.cs`
- Modify: `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessageMap.cs`
- Test: `tests/Application/ArturRios.IdentityManager.Query.Tests/GetPersonByIdQueryHandlerTests.cs`

**Interfaces:**
- Consumes: `IActorScoped` (Task 2).
- Produces:
  - `ArturRios.IdentityManager.Query.Output.PersonOutput : QueryOutput` with `Guid Id`,
    `string Name`, `string Email`, `int Role`, `bool EmailVerified`, `bool IsDeleted`,
    `Guid? ScopeId`, `IEnumerable<Guid> OwnedScopeIds`, `DateTime CreatedAt`, `DateTime UpdatedAt`.
  - `ArturRios.IdentityManager.Query.Input.GetPersonByIdQuery : BaseQuery, IActorScoped` with
    `Guid Id`, `bool IncludeDeleted`.
  - `GetPersonByIdQueryHandler(IAsyncReadOnlyRepository<Person> personReader)` implementing
    `IQueryHandlerAsync<GetPersonByIdQuery, PersonOutput>`.
  - `PersonMessages.PersonRetrievedSuccessfully`, `.PersonsRetrievedSuccessfully`,
    `.PersonNotFound`, `.NotAuthorizedToViewPerson`.

- [ ] **Step 1: Add the four messages**

Append to `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessages.cs`, inside the
class:

```csharp
    /// <summary>UC-07 success: a single person was retrieved.</summary>
    public const string PersonRetrievedSuccessfully = "Person retrieved successfully.";

    /// <summary>UC-07 success: a list of persons was retrieved.</summary>
    public const string PersonsRetrievedSuccessfully = "Persons retrieved successfully.";

    /// <summary>AF-07a: the requested person does not exist (or is logically deleted and not requested).</summary>
    public const string PersonNotFound = "Person not found.";

    /// <summary>AF-07b: the caller is not allowed to view the requested person.</summary>
    public const string NotAuthorizedToViewPerson = "You are not allowed to view this person.";
```

- [ ] **Step 2: Map them to status codes**

Append to the dictionary initializer in
`src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessageMap.cs`, after the existing
`[PersonMessages.InvalidRole]` entry (add a comma to that line):

```csharp
        // UC-07 main flow — person(s) retrieved.
        [PersonMessages.PersonRetrievedSuccessfully] = HttpStatusCodes.Ok,
        [PersonMessages.PersonsRetrievedSuccessfully] = HttpStatusCodes.Ok,
        // AF-07a — person not found.
        [PersonMessages.PersonNotFound] = HttpStatusCodes.NotFound,
        // AF-07b — caller may not view the person.
        [PersonMessages.NotAuthorizedToViewPerson] = HttpStatusCodes.Forbidden
```

Also update the class summary comment to read `following the UC-06 and UC-07 flows`.

- [ ] **Step 3: Create `PersonOutput`**

Create `src/Application/ArturRios.IdentityManager.Query/Output/PersonOutput.cs`:

```csharp
using ArturRios.Mediator.Query;

namespace ArturRios.IdentityManager.Query.Output;

/// <summary>
///     Person data returned by the UC-07 view/list queries. Only externally-facing <c>PublicId</c>
///     identifiers are exposed, and there is deliberately no field for <c>PasswordHash</c> or
///     <c>Salt</c>, so neither can escape through a projection.
/// </summary>
public class PersonOutput : QueryOutput
{
    /// <summary>Public identifier of the person.</summary>
    public Guid Id { get; set; }

    /// <summary>Full name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Email address.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Assigned role value (see <c>Roles</c>).</summary>
    public int Role { get; set; }

    /// <summary>Whether the person's email has been verified.</summary>
    public bool EmailVerified { get; set; }

    /// <summary>Whether the person is logically deleted.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Public identifier of the scope the person belongs to as a User; <c>null</c> otherwise.</summary>
    public Guid? ScopeId { get; set; }

    /// <summary>Public identifiers of the scopes the person owns; empty for non-owners.</summary>
    public IEnumerable<Guid> OwnedScopeIds { get; set; } = new List<Guid>();

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last update timestamp.</summary>
    public DateTime UpdatedAt { get; set; }
}
```

- [ ] **Step 4: Create `GetPersonByIdQuery`**

Create `src/Application/ArturRios.IdentityManager.Query/Input/GetPersonByIdQuery.cs`:

```csharp
using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.IdentityManager.Query.Input;

/// <summary>
///     Request to retrieve a single person by their <c>PublicId</c> (UC-07, FR-PE-03). The pagination
///     members inherited from <see cref="BaseQuery" /> are unused for a by-id lookup.
///     <see cref="IActorScoped.ActingPersonId" />/<see cref="IActorScoped.ActingRole" /> are set by
///     the controller from the authenticated caller, for the AF-07b visibility rule.
/// </summary>
public class GetPersonByIdQuery : BaseQuery, IActorScoped
{
    /// <summary>Public identifier of the person to retrieve.</summary>
    public Guid Id { get; set; }

    /// <summary>When <c>true</c>, a logically deleted person is still returned (FR-PE-08).</summary>
    public bool IncludeDeleted { get; set; }

    public long ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
```

- [ ] **Step 5: Write the failing unit tests**

Create `tests/Application/ArturRios.IdentityManager.Query.Tests/GetPersonByIdQueryHandlerTests.cs`:

```csharp
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.Handlers;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.IdentityManager.Query.Tests;

// Unit tests for GetPersonByIdQueryHandler (UC-07, FR-PE-03/FR-PE-08). Cover the main flow for each
// actor the use case allows, AF-07a (person not found, including logically deleted), AF-07b (caller
// may not view the person), and the include-deleted behavior.
public class GetPersonByIdQueryHandlerTests
{
    private static Scope Scope(long id) => new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}" };

    private static Person User(long id, Scope scope, bool isDeleted = false) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"user-{id}",
        Email = $"user-{id}@test.local",
        RoleId = (long)Roles.User,
        IsDeleted = isDeleted,
        ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope }
    };

    private static Person ScopeAdmin(long id, params Scope[] ownedScopes) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"admin-{id}",
        Email = $"admin-{id}@test.local",
        RoleId = (long)Roles.ScopeAdmin,
        ScopeOwnerships = ownedScopes
            .Select(scope => new ScopeOwner { ScopeId = scope.Id, Scope = scope })
            .ToList()
    };

    private static Person SystemAdmin(long id) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"root-{id}",
        Email = $"root-{id}@test.local",
        RoleId = (long)Roles.SystemAdmin
    };

    private static async Task<AsyncFakeRepository<Person>> RepositoryWith(params Person[] persons)
    {
        var repository = new AsyncFakeRepository<Person>();

        foreach (var person in persons)
        {
            await repository.CreateAsync(person);
        }

        return repository;
    }

    [UnitFact]
    public async Task GivenSystemAdminActor_WhenHandlingGetPersonById_ThenAnyPersonIsReturned()
    {
        // Given
        var scope = Scope(1);
        var target = User(10, scope);
        var repository = await RepositoryWith(target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, ActingPersonId = 99, ActingRole = (int)Roles.SystemAdmin
        });

        // Then
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.Equal(target.PublicId, output.Data!.Id);
        Assert.Equal((int)Roles.User, output.Data.Role);
        Assert.Equal(scope.PublicId, output.Data.ScopeId);
        Assert.Contains(PersonMessages.PersonRetrievedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenUserActorRequestingSelf_WhenHandlingGetPersonById_ThenPersonIsReturned()
    {
        // Given
        var scope = Scope(1);
        var target = User(10, scope);
        var repository = await RepositoryWith(target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, ActingPersonId = target.Id, ActingRole = (int)Roles.User
        });

        // Then
        Assert.True(output.Success);
        Assert.Equal(target.PublicId, output.Data!.Id);
    }

    [UnitFact]
    public async Task GivenUserActorRequestingAnotherPerson_WhenHandlingGetPersonById_ThenReturnsNotAuthorized()
    {
        // Given two Users in the same scope (AF-07b)
        var scope = Scope(1);
        var actor = User(10, scope);
        var target = User(11, scope);
        var repository = await RepositoryWith(actor, target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, ActingPersonId = actor.Id, ActingRole = (int)Roles.User
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotAuthorizedToViewPerson, output.Errors);
    }

    [UnitFact]
    public async Task GivenScopeAdminOwningTargetScope_WhenHandlingGetPersonById_ThenUserIsReturned()
    {
        // Given a ScopeAdmin who owns the scope the target User belongs to
        var scope = Scope(1);
        var actor = ScopeAdmin(10, scope);
        var target = User(11, scope);
        var repository = await RepositoryWith(actor, target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, ActingPersonId = actor.Id, ActingRole = (int)Roles.ScopeAdmin
        });

        // Then
        Assert.True(output.Success);
        Assert.Equal(target.PublicId, output.Data!.Id);
    }

    [UnitFact]
    public async Task GivenScopeAdminNotOwningTargetScope_WhenHandlingGetPersonById_ThenReturnsNotAuthorized()
    {
        // Given a ScopeAdmin who owns a different scope than the target User's (AF-07b)
        var ownedScope = Scope(1);
        var otherScope = Scope(2);
        var actor = ScopeAdmin(10, ownedScope);
        var target = User(11, otherScope);
        var repository = await RepositoryWith(actor, target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, ActingPersonId = actor.Id, ActingRole = (int)Roles.ScopeAdmin
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotAuthorizedToViewPerson, output.Errors);
    }

    [UnitFact]
    public async Task GivenScopeAdminCoOwningScope_WhenHandlingGetPersonById_ThenOtherOwnerIsReturned()
    {
        // Given two ScopeAdmins owning the same scope
        var scope = Scope(1);
        var actor = ScopeAdmin(10, scope);
        var target = ScopeAdmin(11, scope);
        var repository = await RepositoryWith(actor, target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, ActingPersonId = actor.Id, ActingRole = (int)Roles.ScopeAdmin
        });

        // Then
        Assert.True(output.Success);
        Assert.Equal(target.PublicId, output.Data!.Id);
        Assert.Equal([scope.PublicId], output.Data.OwnedScopeIds);
    }

    [UnitFact]
    public async Task GivenScopeAdminRequestingSystemAdmin_WhenHandlingGetPersonById_ThenReturnsNotAuthorized()
    {
        // Given a ScopeAdmin and an unrelated SystemAdmin (AF-07b)
        var scope = Scope(1);
        var actor = ScopeAdmin(10, scope);
        var target = SystemAdmin(11);
        var repository = await RepositoryWith(actor, target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, ActingPersonId = actor.Id, ActingRole = (int)Roles.ScopeAdmin
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotAuthorizedToViewPerson, output.Errors);
    }

    [UnitFact]
    public async Task GivenUnknownPersonId_WhenHandlingGetPersonById_ThenReturnsPersonNotFound()
    {
        // Given an empty store (AF-07a)
        var repository = await RepositoryWith();
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = Guid.NewGuid(), ActingPersonId = 1, ActingRole = (int)Roles.SystemAdmin
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedPersonAndIncludeDeletedFalse_WhenHandlingGetPersonById_ThenReturnsPersonNotFound()
    {
        // Given a logically deleted person (FR-PE-08)
        var scope = Scope(1);
        var target = User(10, scope, isDeleted: true);
        var repository = await RepositoryWith(target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, IncludeDeleted = false,
            ActingPersonId = 1, ActingRole = (int)Roles.SystemAdmin
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedPersonAndIncludeDeletedTrue_WhenHandlingGetPersonById_ThenPersonIsReturned()
    {
        // Given a logically deleted person (FR-PE-08)
        var scope = Scope(1);
        var target = User(10, scope, isDeleted: true);
        var repository = await RepositoryWith(target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, IncludeDeleted = true,
            ActingPersonId = 1, ActingRole = (int)Roles.SystemAdmin
        });

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.IsDeleted);
    }
}
```

- [ ] **Step 6: Run the tests to verify they fail**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"`
Expected: compilation error — `GetPersonByIdQueryHandler` does not exist.

- [ ] **Step 7: Implement the handler**

Create `src/Application/ArturRios.IdentityManager.Query/Handlers/GetPersonByIdQueryHandler.cs`:

```csharp
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Query.Handlers;

/// <summary>
///     Handles <see cref="GetPersonByIdQuery" /> (UC-07, FR-PE-03): retrieves a person by their
///     <c>PublicId</c>, excluding logically deleted persons unless explicitly requested (FR-PE-08),
///     then applies the use case's per-actor visibility rule. A missing person is AF-07a
///     (<c>PersonNotFound</c>); a person the caller may not see is AF-07b
///     (<c>NotAuthorizedToViewPerson</c>). Both are returned as errors rather than thrown.
/// </summary>
public class GetPersonByIdQueryHandler(IAsyncReadOnlyRepository<Person> personReader)
    : IQueryHandlerAsync<GetPersonByIdQuery, PersonOutput>
{
    /// <summary>
    ///     The person plus the internal ids the visibility rule needs. Internal ids never reach the
    ///     caller — only <see cref="Output" /> is returned.
    /// </summary>
    private sealed class PersonProjection
    {
        public long Id { get; init; }
        public long RoleId { get; init; }
        public long? MembershipScopeId { get; init; }
        public List<long> OwnedScopeInternalIds { get; init; } = [];
        public PersonOutput Output { get; init; } = null!;
    }

    public async Task<DataOutput<PersonOutput?>> HandleAsync(GetPersonByIdQuery query)
    {
        var output = DataOutput<PersonOutput?>.New;

        var person = await personReader.Query()
            .Where(x => x.PublicId == query.Id && (query.IncludeDeleted || !x.IsDeleted))
            .Select(x => new PersonProjection
            {
                Id = x.Id,
                RoleId = x.RoleId,
                MembershipScopeId = x.ScopeMembership != null ? x.ScopeMembership.ScopeId : null,
                OwnedScopeInternalIds = x.ScopeOwnerships.Select(ownership => ownership.ScopeId).ToList(),
                Output = new PersonOutput
                {
                    Id = x.PublicId,
                    Name = x.Name,
                    Email = x.Email,
                    Role = (int)x.RoleId,
                    EmailVerified = x.EmailVerified,
                    IsDeleted = x.IsDeleted,
                    ScopeId = x.ScopeMembership != null ? x.ScopeMembership.Scope.PublicId : null,
                    OwnedScopeIds = x.ScopeOwnerships.Select(ownership => ownership.Scope.PublicId).ToList(),
                    CreatedAt = x.CreatedAt,
                    UpdatedAt = x.UpdatedAt
                }
            })
            .FirstOrDefaultAsync();

        // AF-07a: no such person (or it is logically deleted and was not explicitly requested).
        if (person is null)
        {
            return output.WithError(PersonMessages.PersonNotFound);
        }

        // AF-07b: the caller is not allowed to see this person.
        if (!await MayViewAsync(query, person))
        {
            return output.WithError(PersonMessages.NotAuthorizedToViewPerson);
        }

        return output
            .WithData(person.Output)
            .WithMessage(PersonMessages.PersonRetrievedSuccessfully);
    }

    /// <summary>
    ///     UC-07 step 2: a System Admin sees anyone; anyone sees themselves; a Scope Admin sees the
    ///     Users of the scopes they own and the Scope Admins co-owning those scopes. Everything else
    ///     is denied — in particular a User seeing another person, and a Scope Admin seeing a System
    ///     Admin or an unrelated Scope Admin.
    /// </summary>
    private async Task<bool> MayViewAsync(GetPersonByIdQuery query, PersonProjection person)
    {
        if (query.ActingRole == (int)Roles.SystemAdmin || query.ActingPersonId == person.Id)
        {
            return true;
        }

        if (query.ActingRole != (int)Roles.ScopeAdmin)
        {
            return false;
        }

        var ownedScopeIds = await personReader.Query()
            .Where(x => x.Id == query.ActingPersonId)
            .SelectMany(x => x.ScopeOwnerships.Select(ownership => ownership.ScopeId))
            .ToListAsync();

        if (person.RoleId == (long)Roles.User)
        {
            return person.MembershipScopeId is not null && ownedScopeIds.Contains(person.MembershipScopeId.Value);
        }

        return person.RoleId == (long)Roles.ScopeAdmin &&
               person.OwnedScopeInternalIds.Any(ownedScopeIds.Contains);
    }
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"`
Expected: PASS — all ten `GetPersonByIdQueryHandlerTests` green, and no previously passing test broken.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: add get person by id query (UC-07)"
```

---

## Task 4: `GET /api/scopes/{scopeId}/persons` query handler

**Files:**
- Create: `src/Application/ArturRios.IdentityManager.Query/Input/ListScopePersonsQuery.cs`
- Create: `src/Application/ArturRios.IdentityManager.Query/Handlers/ListScopePersonsQueryHandler.cs`
- Modify: `tests/Application/ArturRios.IdentityManager.Query.Tests/ArturRios.IdentityManager.Query.Tests.csproj`
- Test: `tests/Application/ArturRios.IdentityManager.Query.Tests/ListScopePersonsQueryHandlerTests.cs`

**Interfaces:**
- Consumes: `PersonOutput`, `PersonMessages.PersonsRetrievedSuccessfully` (Task 3);
  `IScopeOwnershipChecker` (Task 1); `IActorScoped` (Task 2).
- Produces: `ListScopePersonsQuery : BaseQuery, IActorScoped` with `Guid ScopeId`, `string? Name`,
  `string? Email`, `bool IncludeDeleted`; and
  `ListScopePersonsQueryHandler(IAsyncReadOnlyRepository<Scope> scopeReader, IAsyncReadOnlyRepository<Person> personReader, IScopeOwnershipChecker scopeOwnership)`
  implementing `IPaginatedQueryHandlerAsync<ListScopePersonsQuery, PersonOutput>`.

- [ ] **Step 0: Complete the `Query.Tests` package set**

This task's tests are the first in `Query.Tests` to mock a collaborator, and the project was
scaffolded without Moq or Bogus. Add both — Testing Specification §5 requires the same stack in
every test project — inside the existing `ItemGroup`, after the `ArturRios.Util.Test` reference in
`tests/Application/ArturRios.IdentityManager.Query.Tests/ArturRios.IdentityManager.Query.Tests.csproj`:

```xml
        <PackageReference Include="Bogus" Version="35.6.3" />
        <PackageReference Include="Moq" Version="4.20.72" />
```

- [ ] **Step 1: Create the query**

Create `src/Application/ArturRios.IdentityManager.Query/Input/ListScopePersonsQuery.cs`:

```csharp
using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.IdentityManager.Query.Input;

/// <summary>
///     Request to list the <c>User</c> persons of a scope, with pagination and optional filtering
///     (UC-07, FR-PE-04). <see cref="ScopeId" /> comes from the route;
///     <see cref="IActorScoped.ActingPersonId" />/<see cref="IActorScoped.ActingRole" /> are set by
///     the controller from the authenticated caller and are never taken from the request.
/// </summary>
public class ListScopePersonsQuery : BaseQuery, IActorScoped
{
    /// <summary>Public identifier of the scope whose Users are listed.</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Optional case-insensitive substring filter on the person's name.</summary>
    public string? Name { get; set; }

    /// <summary>Optional case-insensitive substring filter on the person's email.</summary>
    public string? Email { get; set; }

    /// <summary>When <c>true</c>, logically deleted persons are included in the results (FR-PE-08).</summary>
    public bool IncludeDeleted { get; set; }

    public long ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
```

- [ ] **Step 2: Write the failing unit tests**

Create `tests/Application/ArturRios.IdentityManager.Query.Tests/ListScopePersonsQueryHandlerTests.cs`:

```csharp
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.Handlers;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.IdentityManager.Query.Tests;

// Unit tests for ListScopePersonsQueryHandler (UC-07, FR-PE-04/FR-PE-08): the scope's Users only,
// paginated and filterable, gated by scope ownership. Covers the main flow, a missing or logically
// deleted scope (AF-07a), a non-owning actor (AF-07b), and the include-deleted behavior.
public class ListScopePersonsQueryHandlerTests
{
    private static Scope Scope(long id, bool isDeleted = false) =>
        new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}", IsDeleted = isDeleted };

    private static Person User(long id, Scope scope, string name, string email, bool isDeleted = false) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = name,
        Email = email,
        RoleId = (long)Roles.User,
        IsDeleted = isDeleted,
        ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope }
    };

    private static Person Owner(long id, Scope scope) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"owner-{id}",
        Email = $"owner-{id}@test.local",
        RoleId = (long)Roles.ScopeAdmin,
        ScopeOwnerships = [new ScopeOwner { ScopeId = scope.Id, Scope = scope }]
    };

    private static IScopeOwnershipChecker Ownership(bool allowed)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);

        return checker.Object;
    }

    private static async Task<AsyncFakeRepository<Scope>> ScopesWith(params Scope[] scopes)
    {
        var repository = new AsyncFakeRepository<Scope>();

        foreach (var scope in scopes)
        {
            await repository.CreateAsync(scope);
        }

        return repository;
    }

    private static async Task<AsyncFakeRepository<Person>> PersonsWith(params Person[] persons)
    {
        var repository = new AsyncFakeRepository<Person>();

        foreach (var person in persons)
        {
            await repository.CreateAsync(person);
        }

        return repository;
    }

    private static ListScopePersonsQuery QueryFor(Scope scope) => new()
    {
        ScopeId = scope.PublicId,
        PageNumber = 1,
        PageSize = 10,
        ActingPersonId = 1,
        ActingRole = (int)Roles.SystemAdmin
    };

    [UnitFact]
    public async Task GivenScopeWithUsers_WhenHandlingListScopePersons_ThenOnlyItsUsersAreReturned()
    {
        // Given a scope with two Users, an owner, and a User of another scope
        var scope = Scope(1);
        var otherScope = Scope(2);
        var member = User(10, scope, "Ana", "ana@test.local");
        var otherMember = User(11, scope, "Bruno", "bruno@test.local");
        var owner = Owner(12, scope);
        var outsider = User(13, otherScope, "Carla", "carla@test.local");
        var scopes = await ScopesWith(scope, otherScope);
        var persons = await PersonsWith(member, otherMember, owner, outsider);
        var handler = new ListScopePersonsQueryHandler(scopes, persons, Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope));

        // Then
        Assert.True(output.Success);
        Assert.Equal(2, output.TotalItems);
        Assert.Equal([member.PublicId, otherMember.PublicId], output.Data!.Select(x => x.Id));
        Assert.Contains(PersonMessages.PersonsRetrievedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenUnknownScope_WhenHandlingListScopePersons_ThenReturnsScopeNotFound()
    {
        // Given an empty scope store (AF-07a)
        var scopes = await ScopesWith();
        var persons = await PersonsWith();
        var handler = new ListScopePersonsQueryHandler(scopes, persons, Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(new ListScopePersonsQuery
        {
            ScopeId = Guid.NewGuid(), PageNumber = 1, PageSize = 10,
            ActingPersonId = 1, ActingRole = (int)Roles.SystemAdmin
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedScope_WhenHandlingListScopePersons_ThenReturnsScopeNotFound()
    {
        // Given a logically deleted scope (AF-07a)
        var scope = Scope(1, isDeleted: true);
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith();
        var handler = new ListScopePersonsQueryHandler(scopes, persons, Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenActorNotOwningScope_WhenHandlingListScopePersons_ThenReturnsNotScopeOwner()
    {
        // Given an actor the ownership checker rejects (AF-07b)
        var scope = Scope(1);
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(User(10, scope, "Ana", "ana@test.local"));
        var handler = new ListScopePersonsQueryHandler(scopes, persons, Ownership(allowed: false));

        // When
        var output = await handler.HandleAsync(QueryFor(scope));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotScopeOwner, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedUserAndIncludeDeletedFalse_WhenHandlingListScopePersons_ThenUserIsExcluded()
    {
        // Given one active and one logically deleted User (FR-PE-08)
        var scope = Scope(1);
        var active = User(10, scope, "Ana", "ana@test.local");
        var deleted = User(11, scope, "Bruno", "bruno@test.local", isDeleted: true);
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(active, deleted);
        var handler = new ListScopePersonsQueryHandler(scopes, persons, Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope));

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(active.PublicId, Assert.Single(output.Data!).Id);
    }

    [UnitFact]
    public async Task GivenDeletedUserAndIncludeDeletedTrue_WhenHandlingListScopePersons_ThenUserIsIncluded()
    {
        // Given one active and one logically deleted User (FR-PE-08)
        var scope = Scope(1);
        var active = User(10, scope, "Ana", "ana@test.local");
        var deleted = User(11, scope, "Bruno", "bruno@test.local", isDeleted: true);
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(active, deleted);
        var handler = new ListScopePersonsQueryHandler(scopes, persons, Ownership(allowed: true));
        var query = QueryFor(scope);
        query.IncludeDeleted = true;

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal(2, output.TotalItems);
    }

    [UnitFact]
    public async Task GivenNameFilter_WhenHandlingListScopePersons_ThenOnlyMatchingUsersAreReturned()
    {
        // Given two Users with different names; the filter is case-insensitive
        var scope = Scope(1);
        var ana = User(10, scope, "Ana", "ana@test.local");
        var bruno = User(11, scope, "Bruno", "bruno@test.local");
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(ana, bruno);
        var handler = new ListScopePersonsQueryHandler(scopes, persons, Ownership(allowed: true));
        var query = QueryFor(scope);
        query.Name = "an";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(ana.PublicId, Assert.Single(output.Data!).Id);
    }

    [UnitFact]
    public async Task GivenEmailFilter_WhenHandlingListScopePersons_ThenOnlyMatchingUsersAreReturned()
    {
        // Given two Users with different emails; the filter is case-insensitive
        var scope = Scope(1);
        var ana = User(10, scope, "Ana", "ana@test.local");
        var bruno = User(11, scope, "Bruno", "bruno@test.local");
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(ana, bruno);
        var handler = new ListScopePersonsQueryHandler(scopes, persons, Ownership(allowed: true));
        var query = QueryFor(scope);
        query.Email = "BRUNO@";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(bruno.PublicId, Assert.Single(output.Data!).Id);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"`
Expected: compilation error — `ListScopePersonsQueryHandler` does not exist.

- [ ] **Step 4: Implement the handler**

Create `src/Application/ArturRios.IdentityManager.Query/Handlers/ListScopePersonsQueryHandler.cs`:

```csharp
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Query.Handlers;

/// <summary>
///     Handles <see cref="ListScopePersonsQuery" /> (UC-07, FR-PE-04): lists the <c>User</c> persons
///     of a scope with pagination and optional name/email filters, excluding logically deleted
///     persons unless explicitly requested (FR-PE-08). A missing or logically deleted scope is AF-07a
///     (<c>ScopeNotFound</c>); an actor who does not own the scope is AF-07b (<c>NotScopeOwner</c>).
///     A System Admin bypasses the ownership check.
/// </summary>
public class ListScopePersonsQueryHandler(
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncReadOnlyRepository<Person> personReader,
    IScopeOwnershipChecker scopeOwnership)
    : IPaginatedQueryHandlerAsync<ListScopePersonsQuery, PersonOutput>
{
    public async Task<PaginatedOutput<PersonOutput>> HandleAsync(ListScopePersonsQuery query)
    {
        var output = PaginatedOutput<PersonOutput>.New;

        // AF-07a: the target scope must exist and not be logically deleted.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == query.ScopeId && !x.IsDeleted);

        if (scope is null)
        {
            return output.WithError(PersonMessages.ScopeNotFound);
        }

        // AF-07b: a Scope Admin may only read a scope they own; a System Admin bypasses.
        if (!await scopeOwnership.ActorMayManageScopeAsync(query.ActingRole, query.ActingPersonId, scope.Id))
        {
            return output.WithError(PersonMessages.NotScopeOwner);
        }

        var persons = personReader.Query()
            .Where(x => x.ScopeMembership != null && x.ScopeMembership.ScopeId == scope.Id);

        if (!query.IncludeDeleted)
        {
            persons = persons.Where(x => !x.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var name = query.Name.ToLower();
            persons = persons.Where(x => x.Name.ToLower().Contains(name));
        }

        if (!string.IsNullOrWhiteSpace(query.Email))
        {
            var email = query.Email.ToLower();
            persons = persons.Where(x => x.Email.ToLower().Contains(email));
        }

        var projected = persons.Select(x => new PersonOutput
        {
            Id = x.PublicId,
            Name = x.Name,
            Email = x.Email,
            Role = (int)x.RoleId,
            EmailVerified = x.EmailVerified,
            IsDeleted = x.IsDeleted,
            ScopeId = x.ScopeMembership != null ? x.ScopeMembership.Scope.PublicId : null,
            OwnedScopeIds = x.ScopeOwnerships.Select(ownership => ownership.Scope.PublicId).ToList(),
            CreatedAt = x.CreatedAt,
            UpdatedAt = x.UpdatedAt
        });

        var page = await projected.PaginateAsync(query.PageNumber, query.PageSize, x => x.Name);

        return page.WithMessage(PersonMessages.PersonsRetrievedSuccessfully);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"`
Expected: PASS — all eight `ListScopePersonsQueryHandlerTests` green, nothing else broken.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add list scope persons query (UC-07)"
```

---

## Task 5: `GET /api/scopes/{scopeId}/owners` query handler

Same shape as Task 4, selecting `SCOPE_OWNER` rows instead of `SCOPE_USER` ones.

**Files:**
- Create: `src/Application/ArturRios.IdentityManager.Query/Input/ListScopeOwnersQuery.cs`
- Create: `src/Application/ArturRios.IdentityManager.Query/Handlers/ListScopeOwnersQueryHandler.cs`
- Test: `tests/Application/ArturRios.IdentityManager.Query.Tests/ListScopeOwnersQueryHandlerTests.cs`

**Interfaces:**
- Consumes: `PersonOutput`, `PersonMessages.PersonsRetrievedSuccessfully` (Task 3);
  `IScopeOwnershipChecker` (Task 1); `IActorScoped` (Task 2).
- Produces: `ListScopeOwnersQuery : BaseQuery, IActorScoped` with `Guid ScopeId`, `string? Name`,
  `string? Email`, `bool IncludeDeleted`; and
  `ListScopeOwnersQueryHandler(IAsyncReadOnlyRepository<Scope> scopeReader, IAsyncReadOnlyRepository<Person> personReader, IScopeOwnershipChecker scopeOwnership)`
  implementing `IPaginatedQueryHandlerAsync<ListScopeOwnersQuery, PersonOutput>`.

- [ ] **Step 1: Create the query**

Create `src/Application/ArturRios.IdentityManager.Query/Input/ListScopeOwnersQuery.cs`:

```csharp
using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.IdentityManager.Query.Input;

/// <summary>
///     Request to list the <c>ScopeAdmin</c> owners of a scope, with pagination and optional
///     filtering (UC-07, FR-PE-04). <see cref="ScopeId" /> comes from the route;
///     <see cref="IActorScoped.ActingPersonId" />/<see cref="IActorScoped.ActingRole" /> are set by
///     the controller from the authenticated caller and are never taken from the request.
/// </summary>
public class ListScopeOwnersQuery : BaseQuery, IActorScoped
{
    /// <summary>Public identifier of the scope whose owners are listed.</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Optional case-insensitive substring filter on the owner's name.</summary>
    public string? Name { get; set; }

    /// <summary>Optional case-insensitive substring filter on the owner's email.</summary>
    public string? Email { get; set; }

    /// <summary>When <c>true</c>, logically deleted owners are included in the results (FR-PE-08).</summary>
    public bool IncludeDeleted { get; set; }

    public long ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
```

- [ ] **Step 2: Write the failing unit tests**

Create `tests/Application/ArturRios.IdentityManager.Query.Tests/ListScopeOwnersQueryHandlerTests.cs`:

```csharp
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.Handlers;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.IdentityManager.Query.Tests;

// Unit tests for ListScopeOwnersQueryHandler (UC-07, FR-PE-04/FR-PE-08): the scope's ScopeAdmin
// owners only, paginated and filterable, gated by scope ownership. Covers the main flow, a missing
// or logically deleted scope (AF-07a), a non-owning actor (AF-07b), and include-deleted behavior.
public class ListScopeOwnersQueryHandlerTests
{
    private static Scope Scope(long id, bool isDeleted = false) =>
        new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}", IsDeleted = isDeleted };

    private static Person Owner(long id, Scope scope, string name, string email, bool isDeleted = false) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = name,
        Email = email,
        RoleId = (long)Roles.ScopeAdmin,
        IsDeleted = isDeleted,
        ScopeOwnerships = [new ScopeOwner { ScopeId = scope.Id, Scope = scope }]
    };

    private static Person Member(long id, Scope scope) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"user-{id}",
        Email = $"user-{id}@test.local",
        RoleId = (long)Roles.User,
        ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope }
    };

    private static IScopeOwnershipChecker Ownership(bool allowed)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);

        return checker.Object;
    }

    private static async Task<AsyncFakeRepository<Scope>> ScopesWith(params Scope[] scopes)
    {
        var repository = new AsyncFakeRepository<Scope>();

        foreach (var scope in scopes)
        {
            await repository.CreateAsync(scope);
        }

        return repository;
    }

    private static async Task<AsyncFakeRepository<Person>> PersonsWith(params Person[] persons)
    {
        var repository = new AsyncFakeRepository<Person>();

        foreach (var person in persons)
        {
            await repository.CreateAsync(person);
        }

        return repository;
    }

    private static ListScopeOwnersQuery QueryFor(Scope scope) => new()
    {
        ScopeId = scope.PublicId,
        PageNumber = 1,
        PageSize = 10,
        ActingPersonId = 1,
        ActingRole = (int)Roles.SystemAdmin
    };

    [UnitFact]
    public async Task GivenScopeWithOwners_WhenHandlingListScopeOwners_ThenOnlyItsOwnersAreReturned()
    {
        // Given a scope with two owners, one User, and an owner of another scope
        var scope = Scope(1);
        var otherScope = Scope(2);
        var ana = Owner(10, scope, "Ana", "ana@test.local");
        var bruno = Owner(11, scope, "Bruno", "bruno@test.local");
        var member = Member(12, scope);
        var outsider = Owner(13, otherScope, "Carla", "carla@test.local");
        var scopes = await ScopesWith(scope, otherScope);
        var persons = await PersonsWith(ana, bruno, member, outsider);
        var handler = new ListScopeOwnersQueryHandler(scopes, persons, Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope));

        // Then
        Assert.True(output.Success);
        Assert.Equal(2, output.TotalItems);
        Assert.Equal([ana.PublicId, bruno.PublicId], output.Data!.Select(x => x.Id));
        Assert.Contains(PersonMessages.PersonsRetrievedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenUnknownScope_WhenHandlingListScopeOwners_ThenReturnsScopeNotFound()
    {
        // Given an empty scope store (AF-07a)
        var scopes = await ScopesWith();
        var persons = await PersonsWith();
        var handler = new ListScopeOwnersQueryHandler(scopes, persons, Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(new ListScopeOwnersQuery
        {
            ScopeId = Guid.NewGuid(), PageNumber = 1, PageSize = 10,
            ActingPersonId = 1, ActingRole = (int)Roles.SystemAdmin
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedScope_WhenHandlingListScopeOwners_ThenReturnsScopeNotFound()
    {
        // Given a logically deleted scope (AF-07a)
        var scope = Scope(1, isDeleted: true);
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith();
        var handler = new ListScopeOwnersQueryHandler(scopes, persons, Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenActorNotOwningScope_WhenHandlingListScopeOwners_ThenReturnsNotScopeOwner()
    {
        // Given an actor the ownership checker rejects (AF-07b)
        var scope = Scope(1);
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(Owner(10, scope, "Ana", "ana@test.local"));
        var handler = new ListScopeOwnersQueryHandler(scopes, persons, Ownership(allowed: false));

        // When
        var output = await handler.HandleAsync(QueryFor(scope));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotScopeOwner, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedOwnerAndIncludeDeletedFalse_WhenHandlingListScopeOwners_ThenOwnerIsExcluded()
    {
        // Given one active and one logically deleted owner (FR-PE-08)
        var scope = Scope(1);
        var active = Owner(10, scope, "Ana", "ana@test.local");
        var deleted = Owner(11, scope, "Bruno", "bruno@test.local", isDeleted: true);
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(active, deleted);
        var handler = new ListScopeOwnersQueryHandler(scopes, persons, Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope));

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(active.PublicId, Assert.Single(output.Data!).Id);
    }

    [UnitFact]
    public async Task GivenDeletedOwnerAndIncludeDeletedTrue_WhenHandlingListScopeOwners_ThenOwnerIsIncluded()
    {
        // Given one active and one logically deleted owner (FR-PE-08)
        var scope = Scope(1);
        var active = Owner(10, scope, "Ana", "ana@test.local");
        var deleted = Owner(11, scope, "Bruno", "bruno@test.local", isDeleted: true);
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(active, deleted);
        var handler = new ListScopeOwnersQueryHandler(scopes, persons, Ownership(allowed: true));
        var query = QueryFor(scope);
        query.IncludeDeleted = true;

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal(2, output.TotalItems);
    }

    [UnitFact]
    public async Task GivenNameFilter_WhenHandlingListScopeOwners_ThenOnlyMatchingOwnersAreReturned()
    {
        // Given two owners with different names; the filter is case-insensitive
        var scope = Scope(1);
        var ana = Owner(10, scope, "Ana", "ana@test.local");
        var bruno = Owner(11, scope, "Bruno", "bruno@test.local");
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(ana, bruno);
        var handler = new ListScopeOwnersQueryHandler(scopes, persons, Ownership(allowed: true));
        var query = QueryFor(scope);
        query.Name = "AN";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(ana.PublicId, Assert.Single(output.Data!).Id);
    }

    [UnitFact]
    public async Task GivenEmailFilter_WhenHandlingListScopeOwners_ThenOnlyMatchingOwnersAreReturned()
    {
        // Given two owners with different emails; the filter is case-insensitive
        var scope = Scope(1);
        var ana = Owner(10, scope, "Ana", "ana@test.local");
        var bruno = Owner(11, scope, "Bruno", "bruno@test.local");
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(ana, bruno);
        var handler = new ListScopeOwnersQueryHandler(scopes, persons, Ownership(allowed: true));
        var query = QueryFor(scope);
        query.Email = "BRUNO@";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(bruno.PublicId, Assert.Single(output.Data!).Id);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"`
Expected: compilation error — `ListScopeOwnersQueryHandler` does not exist.

- [ ] **Step 4: Implement the handler**

Create `src/Application/ArturRios.IdentityManager.Query/Handlers/ListScopeOwnersQueryHandler.cs`:

```csharp
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Query.Handlers;

/// <summary>
///     Handles <see cref="ListScopeOwnersQuery" /> (UC-07, FR-PE-04): lists the <c>ScopeAdmin</c>
///     owners of a scope with pagination and optional name/email filters, excluding logically deleted
///     persons unless explicitly requested (FR-PE-08). A missing or logically deleted scope is AF-07a
///     (<c>ScopeNotFound</c>); an actor who does not own the scope is AF-07b (<c>NotScopeOwner</c>).
///     A System Admin bypasses the ownership check.
/// </summary>
public class ListScopeOwnersQueryHandler(
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncReadOnlyRepository<Person> personReader,
    IScopeOwnershipChecker scopeOwnership)
    : IPaginatedQueryHandlerAsync<ListScopeOwnersQuery, PersonOutput>
{
    public async Task<PaginatedOutput<PersonOutput>> HandleAsync(ListScopeOwnersQuery query)
    {
        var output = PaginatedOutput<PersonOutput>.New;

        // AF-07a: the target scope must exist and not be logically deleted.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == query.ScopeId && !x.IsDeleted);

        if (scope is null)
        {
            return output.WithError(PersonMessages.ScopeNotFound);
        }

        // AF-07b: a Scope Admin may only read a scope they own; a System Admin bypasses.
        if (!await scopeOwnership.ActorMayManageScopeAsync(query.ActingRole, query.ActingPersonId, scope.Id))
        {
            return output.WithError(PersonMessages.NotScopeOwner);
        }

        var owners = personReader.Query()
            .Where(x => x.ScopeOwnerships.Any(ownership => ownership.ScopeId == scope.Id));

        if (!query.IncludeDeleted)
        {
            owners = owners.Where(x => !x.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var name = query.Name.ToLower();
            owners = owners.Where(x => x.Name.ToLower().Contains(name));
        }

        if (!string.IsNullOrWhiteSpace(query.Email))
        {
            var email = query.Email.ToLower();
            owners = owners.Where(x => x.Email.ToLower().Contains(email));
        }

        var projected = owners.Select(x => new PersonOutput
        {
            Id = x.PublicId,
            Name = x.Name,
            Email = x.Email,
            Role = (int)x.RoleId,
            EmailVerified = x.EmailVerified,
            IsDeleted = x.IsDeleted,
            ScopeId = x.ScopeMembership != null ? x.ScopeMembership.Scope.PublicId : null,
            OwnedScopeIds = x.ScopeOwnerships.Select(ownership => ownership.Scope.PublicId).ToList(),
            CreatedAt = x.CreatedAt,
            UpdatedAt = x.UpdatedAt
        });

        var page = await projected.PaginateAsync(query.PageNumber, query.PageSize, x => x.Name);

        return page.WithMessage(PersonMessages.PersonsRetrievedSuccessfully);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"`
Expected: PASS — all eight `ListScopeOwnersQueryHandlerTests` green, nothing else broken.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add list scope owners query (UC-07)"
```

---

## Task 6: Expose the three endpoints and cover `GET /api/persons/{id}` end to end

Wires all three routes and their DI registrations — the later two tasks then only add functional
coverage against routes that already exist.

**Files:**
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/Controllers/PersonController.cs`
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs`
- Test: `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerGetByIdTests.cs`

**Interfaces:**
- Consumes: all three queries, `PersonOutput`, the handlers (Tasks 3–5), `IActorScoped` (Task 2).
- Produces: `GET /api/persons/{id}`, `GET /api/scopes/{scopeId}/persons`,
  `GET /api/scopes/{scopeId}/owners`.

> **Ordering note.** Unlike Tasks 3–5 and 7–8, this task is not test-first: the routes and their DI
> registrations go in before the functional tests are written. A functional test cannot fail for the
> right reason against a route that does not exist yet — it reports `404` for every case, including
> the cases that expect `404` — so the failing run would prove nothing. The tests are still written
> and run before the task is committed.

- [ ] **Step 1: Add the three controller actions**

In `src/Presentation/ArturRios.IdentityManager.WebApi/Controllers/PersonController.cs`, extend the
using block with:

```csharp
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.Mediator.Query;
```

Change the constructor to take the query mediator too:

```csharp
public class PersonController(CommandMediator commandMediator, QueryMediator queryMediator) : Controller
```

Then add the three actions above the private `ApplyActor` helper:

```csharp
    /// <summary>
    ///     Retrieves a single person by their public identifier (UC-07, FR-PE-03). Open to any
    ///     authenticated actor; the per-actor visibility rule (AF-07b) is data-dependent and is
    ///     therefore enforced by the handler.
    /// </summary>
    [HttpGet("persons/{id:guid}")]
    public async Task<ActionResult<DataOutput<PersonOutput?>>> GetById(
        Guid id, [FromQuery] bool includeDeleted = false)
    {
        var query = new GetPersonByIdQuery { Id = id, IncludeDeleted = includeDeleted };
        ApplyActor(query);

        var result = await queryMediator.ExecuteQueryAsync<GetPersonByIdQuery, PersonOutput>(query);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Lists the <c>User</c> persons of a scope (UC-07, FR-PE-04). A System Admin or an owner of
    ///     the scope may call it; the ownership check (AF-07b) is enforced by the handler from the
    ///     acting user.
    /// </summary>
    [HttpGet("scopes/{scopeId:guid}/persons")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<PaginatedOutput<PersonOutput>>> ListScopePersons(
        Guid scopeId, [FromQuery] ListScopePersonsQuery query)
    {
        query.ScopeId = scopeId;
        ApplyActor(query);

        var result = await queryMediator
            .ExecutePaginatedQueryAsync<ListScopePersonsQuery, PersonOutput>(query);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Lists the <c>ScopeAdmin</c> owners of a scope (UC-07, FR-PE-04). A System Admin or an
    ///     owner of the scope may call it; the ownership check (AF-07b) is enforced by the handler
    ///     from the acting user.
    /// </summary>
    [HttpGet("scopes/{scopeId:guid}/owners")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<PaginatedOutput<PersonOutput>>> ListScopeOwners(
        Guid scopeId, [FromQuery] ListScopeOwnersQuery query)
    {
        query.ScopeId = scopeId;
        ApplyActor(query);

        var result = await queryMediator
            .ExecutePaginatedQueryAsync<ListScopeOwnersQuery, PersonOutput>(query);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }
```

`ApplyActor` runs after model binding, so any `actingPersonId` / `actingRole` a caller puts in the
query string is overwritten by the token's values.

- [ ] **Step 2: Register the three handlers**

In `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs`, immediately after the existing
`ListScopesQuery` registration, add:

```csharp
        Builder.Services
            .AddScoped<IQueryHandlerAsync<GetPersonByIdQuery, PersonOutput>, GetPersonByIdQueryHandler>();
        Builder.Services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopePersonsQuery, PersonOutput>, ListScopePersonsQueryHandler>();
        Builder.Services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopeOwnersQuery, PersonOutput>, ListScopeOwnersQueryHandler>();
```

- [ ] **Step 3: Write the functional tests**

Create `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerGetByIdTests.cs`:

```csharp
using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.IdentityManager.WebApi.Tests;

// Functional tests for GET /api/persons/{id} (UC-07, FR-PE-03/FR-PE-08): the main flow for each
// actor the use case allows, AF-07a (404), AF-07b (403), and the unauthenticated flow (401).
[Collection(nameof(FunctionalCollection))]
public class PersonControllerGetByIdTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedUserAsync(Scope scope, bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Member",
            Email = $"user-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.User,
            IsDeleted = isDeleted
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<Person> SeedScopeAdminAsync(Scope? ownedScope = null)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Admin",
            Email = $"admin-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.ScopeAdmin,
            EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        if (ownedScope is not null)
        {
            context.ScopeOwners.Add(new ScopeOwner { ScopeId = ownedScope.Id, PersonId = person.Id });
            await context.SaveChangesAsync();
        }

        return person;
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenGetPersonById_ThenReturnsPersonWithoutSecrets()
    {
        // Given
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{person.PublicId}");

        // Then — response carries the person's public data
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(person.PublicId, response.Body?.Data?.Id);
        Assert.Equal(person.Email, response.Body?.Data?.Email);
        Assert.Equal((int)Roles.User, response.Body?.Data?.Role);
        Assert.Equal(scope.PublicId, response.Body?.Data?.ScopeId);

        // Then — the response type has no field for credential material, so the hash and salt held
        // in the database cannot have travelled with it
        await using var context = db.CreateContext();
        var stored = await context.Persons.AsNoTracking().FirstAsync(p => p.PublicId == person.PublicId);
        Assert.Equal(person.PublicId, stored.PublicId);
    }

    [FunctionalFact]
    public async Task GivenUserRequestingSelf_WhenGetPersonById_ThenReturnsPerson()
    {
        // Given a User authenticated as themselves
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.For((int)person.Id, (int)Roles.User));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{person.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(person.PublicId, response.Body?.Data?.Id);
    }

    [FunctionalFact]
    public async Task GivenUserRequestingAnotherPerson_WhenGetPersonById_ThenForbidden()
    {
        // Given two Users of the same scope (AF-07b)
        var scope = await SeedScopeAsync();
        var actor = await SeedUserAsync(scope);
        var target = await SeedUserAsync(scope);
        Authorize(TestTokens.For((int)actor.Id, (int)Roles.User));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{target.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenOwnerScopeAdmin_WhenGetPersonById_ThenReturnsScopeUser()
    {
        // Given a ScopeAdmin who owns the User's scope
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var target = await SeedUserAsync(scope);
        Authorize(TestTokens.For((int)owner.Id, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{target.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(target.PublicId, response.Body?.Data?.Id);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwningScope_WhenGetPersonById_ThenForbidden()
    {
        // Given a ScopeAdmin who does not own the User's scope (AF-07b)
        var scope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync();
        var target = await SeedUserAsync(scope);
        Authorize(TestTokens.For((int)admin.Id, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{target.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnknownPersonId_WhenGetPersonById_ThenNotFound()
    {
        // Given (AF-07a)
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{Guid.NewGuid()}");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDeletedPerson_WhenGetPersonByIdWithoutIncludeDeleted_ThenNotFound()
    {
        // Given a logically deleted person (FR-PE-08)
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{person.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDeletedPerson_WhenGetPersonByIdWithIncludeDeleted_ThenReturnsPerson()
    {
        // Given a logically deleted person (FR-PE-08)
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>(
            $"/api/persons/{person.PublicId}?includeDeleted=true");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body?.Data?.IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenGetPersonById_ThenUnauthorized()
    {
        // Given a person but no bearer token on the gateway
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{person.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 4: Run the functional tests to verify they pass**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Functional"`
Expected: PASS — all nine `PersonControllerGetByIdTests` green, and the existing functional suites
(`ScopeController*`, `PersonControllerCreate*`, `HealthCheckTests`, `SchemaTests`, `SeedingTests`)
still green.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: expose person view endpoints (UC-07)"
```

---

## Task 7: Functional coverage for `GET /api/scopes/{scopeId}/persons`

**Files:**
- Test: `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerListScopePersonsTests.cs`

**Interfaces:**
- Consumes: the route and DI wiring from Task 6; `PersonOutput` from Task 3.
- Produces: nothing further tasks depend on.

- [ ] **Step 1: Write the functional tests**

Create `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerListScopePersonsTests.cs`:

```csharp
using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.IdentityManager.WebApi.Tests;

// Functional tests for GET /api/scopes/{scopeId}/persons (UC-07, FR-PE-04): the main flow for a
// System Admin and an owning Scope Admin, AF-07a (unknown scope → 404), AF-07b (non-owner → 403),
// and the framework-level authorization flows (403 for a plain User, 401 unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class PersonControllerListScopePersonsTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedUserAsync(Scope scope, string name)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = name,
            Email = $"user-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.User
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<Person> SeedScopeAdminAsync(Scope? ownedScope = null)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Admin",
            Email = $"admin-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.ScopeAdmin,
            EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        if (ownedScope is not null)
        {
            context.ScopeOwners.Add(new ScopeOwner { ScopeId = ownedScope.Id, PersonId = person.Id });
            await context.SaveChangesAsync();
        }

        return person;
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenGetScopePersons_ThenReturnsOnlyThatScopesUsers()
    {
        // Given two scopes, each with a User
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var member = await SeedUserAsync(scope, "Ana");
        await SeedUserAsync(otherScope, "Carla");
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?pageNumber=1&pageSize=10");

        // Then — only the scope's User, not its owner and not the other scope's User
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.TotalItems);
        var returned = Assert.Single(response.Body!.Data!);
        Assert.Equal(member.PublicId, returned.Id);
        Assert.NotEqual(owner.PublicId, returned.Id);
        Assert.Equal(scope.PublicId, returned.ScopeId);
    }

    [FunctionalFact]
    public async Task GivenOwnerScopeAdmin_WhenGetScopePersons_ThenReturnsUsers()
    {
        // Given a ScopeAdmin who owns the scope
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        await SeedUserAsync(scope, "Ana");
        Authorize(TestTokens.For((int)owner.Id, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.TotalItems);
    }

    [FunctionalFact]
    public async Task GivenNameFilter_WhenGetScopePersons_ThenReturnsMatchingUserOnly()
    {
        // Given two Users in the scope
        var scope = await SeedScopeAsync();
        var ana = await SeedUserAsync(scope, "Ana");
        await SeedUserAsync(scope, "Bruno");
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?name=ana&pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ana.PublicId, Assert.Single(response.Body!.Data!).Id);
    }

    [FunctionalFact]
    public async Task GivenForgedActorInQueryString_WhenGetScopePersons_ThenTokenActorWins()
    {
        // Given a non-owning ScopeAdmin trying to impersonate a System Admin through the query string
        var scope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync();
        Authorize(TestTokens.For((int)admin.Id, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?actingRole=1&actingPersonId=1&pageNumber=1&pageSize=10");

        // Then — the forged values are discarded and the real actor is rejected (AF-07b)
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwningScope_WhenGetScopePersons_ThenForbidden()
    {
        // Given a ScopeAdmin who does not own the scope (AF-07b)
        var scope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync();
        Authorize(TestTokens.For((int)admin.Id, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnknownScope_WhenGetScopePersons_ThenNotFound()
    {
        // Given (AF-07a)
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{Guid.NewGuid()}/persons?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenPlainUserCaller_WhenGetScopePersons_ThenForbidden()
    {
        // Given a User, whom the role gate rejects before the handler runs
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenGetScopePersons_ThenUnauthorized()
    {
        // Given a scope but no bearer token on the gateway
        var scope = await SeedScopeAsync();

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run the functional tests**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Functional"`
Expected: PASS — all eight `PersonControllerListScopePersonsTests` green, nothing else broken.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "test: cover list scope persons endpoint (UC-07)"
```

---

## Task 8: Functional coverage for `GET /api/scopes/{scopeId}/owners`

**Files:**
- Test: `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerListScopeOwnersTests.cs`

**Interfaces:**
- Consumes: the route and DI wiring from Task 6; `PersonOutput` from Task 3.
- Produces: nothing further tasks depend on.

- [ ] **Step 1: Write the functional tests**

Create `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerListScopeOwnersTests.cs`:

```csharp
using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.IdentityManager.WebApi.Tests;

// Functional tests for GET /api/scopes/{scopeId}/owners (UC-07, FR-PE-04): the main flow for a
// System Admin and an owning Scope Admin, AF-07a (unknown scope → 404), AF-07b (non-owner → 403),
// and the framework-level authorization flows (403 for a plain User, 401 unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class PersonControllerListScopeOwnersTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedUserAsync(Scope scope)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Member",
            Email = $"user-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.User
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<Person> SeedScopeAdminAsync(Scope? ownedScope = null, string name = "Admin")
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = name,
            Email = $"admin-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.ScopeAdmin,
            EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        if (ownedScope is not null)
        {
            context.ScopeOwners.Add(new ScopeOwner { ScopeId = ownedScope.Id, PersonId = person.Id });
            await context.SaveChangesAsync();
        }

        return person;
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenGetScopeOwners_ThenReturnsOnlyThatScopesOwners()
    {
        // Given a scope with one owner and one User, plus an owner of another scope
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        await SeedScopeAdminAsync(ownedScope: otherScope);
        var member = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/owners?pageNumber=1&pageSize=10");

        // Then — only the scope's owner, not its User and not the other scope's owner
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.TotalItems);
        var returned = Assert.Single(response.Body!.Data!);
        Assert.Equal(owner.PublicId, returned.Id);
        Assert.NotEqual(member.PublicId, returned.Id);
        Assert.Contains(scope.PublicId, returned.OwnedScopeIds);
    }

    [FunctionalFact]
    public async Task GivenOwnerScopeAdmin_WhenGetScopeOwners_ThenReturnsCoOwners()
    {
        // Given two ScopeAdmins owning the same scope
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope, name: "Ana");
        await SeedScopeAdminAsync(ownedScope: scope, name: "Bruno");
        Authorize(TestTokens.For((int)owner.Id, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/owners?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, response.Body?.TotalItems);
    }

    [FunctionalFact]
    public async Task GivenNameFilter_WhenGetScopeOwners_ThenReturnsMatchingOwnerOnly()
    {
        // Given two owners of the scope
        var scope = await SeedScopeAsync();
        var ana = await SeedScopeAdminAsync(ownedScope: scope, name: "Ana");
        await SeedScopeAdminAsync(ownedScope: scope, name: "Bruno");
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/owners?name=ana&pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ana.PublicId, Assert.Single(response.Body!.Data!).Id);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwningScope_WhenGetScopeOwners_ThenForbidden()
    {
        // Given a ScopeAdmin who does not own the scope (AF-07b)
        var scope = await SeedScopeAsync();
        await SeedScopeAdminAsync(ownedScope: scope);
        var outsider = await SeedScopeAdminAsync();
        Authorize(TestTokens.For((int)outsider.Id, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/owners?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnknownScope_WhenGetScopeOwners_ThenNotFound()
    {
        // Given (AF-07a)
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{Guid.NewGuid()}/owners?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenPlainUserCaller_WhenGetScopeOwners_ThenForbidden()
    {
        // Given a User, whom the role gate rejects before the handler runs
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/owners?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenGetScopeOwners_ThenUnauthorized()
    {
        // Given a scope but no bearer token on the gateway
        var scope = await SeedScopeAsync();

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/owners?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run the full suite**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"`
Expected: PASS.

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Functional"`
Expected: PASS — all seven `PersonControllerListScopeOwnersTests` green, nothing else broken.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "test: cover list scope owners endpoint (UC-07)"
```

---

## Definition of Done check (Development Workflow Document §5)

Walk this before opening the pull request:

- [ ] Implemented on `feature/uc-07-view-person`, branched from an up-to-date `main`.
- [ ] Main flow implemented for all three endpoints; AF-07a and AF-07b implemented on each.
- [ ] Unit tests cover each of the three query handlers (main + applicable `AF-xx`), and the
      relocated `ScopeOwnershipCheckerTests` still pass from `Shared.Tests`.
- [ ] Functional tests cover each endpoint, including the authorization flows (403 and 401).
- [ ] `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"` and
      `--filter "Category=Functional"` both pass — real output read, not assumed.
- [ ] Pull request opened into `main` with `Closes #8` in the description, awaiting human review.
