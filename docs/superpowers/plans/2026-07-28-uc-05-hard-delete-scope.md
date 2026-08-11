# UC-05: Hard Delete Scope Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a System Admin permanently delete a scope via `DELETE /api/scopes/{id}/hard`, permanently removing the scope's Users, Google Users, applications, and its `SCOPE_OWNER`/`SCOPE_USER` join rows (but not the owner person records), and returning the member totals.

**Architecture:** Mirror the existing UC-04 write flow — a `HardDeleteScopeCommand` dispatched through `CommandMediator` to a `HardDeleteScopeCommandHandler` that returns a `DataOutput<HardDeleteScopeCommandOutput?>`; a thin controller action; canonical messages mapped to HTTP status codes. The handler depends on read-only + writable repositories for `Scope`, `Person`, `GoogleUser`, and `Application`, sharing the scoped `AppDbContext`. It deletes applications, Google Users, and User persons explicitly, then deletes the scope, whose DB `ON DELETE CASCADE` clears the `SCOPE_OWNER`/`SCOPE_USER` join rows.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core (PostgreSQL), xUnit + `AsyncFakeRepository` (ArturRios.Util.Test) + Bogus for unit tests, Testcontainers PostgreSQL for functional tests.

## Global Constraints

- Handlers return `DataOutput<T>` and never throw; failures are added as errors using a canonical `ScopeMessages` constant (System Requirements / repo pattern).
- Public vs internal IDs: inputs/outputs/routes use `PublicId` (GUID); foreign keys, joins, and `DeleteRangeAsync` ids use internal `Id` (bigint). Never expose or accept internal `Id` (System Requirements §4.0 / NFR-15).
- No EF migration — this change removes rows through existing `ON DELETE CASCADE` foreign keys and explicit deletes; do **not** generate or edit migrations.
- Owner **person** records (`ScopeAdmin`s) are never deleted; only their `SCOPE_OWNER` join rows are removed, via the scope-delete cascade.
- Count semantics: `DeletedUserCount` / `DeletedGoogleUserCount` / `DeletedApplicationCount` are the totals of the scope's members regardless of their individual `IsDeleted` state.
- Deletion order is applications → Google Users → User persons → scope, so no foreign key is ever violated (`application.owner_id → person` is the only inter-member FK).
- Test naming is Given-When-Then (`GivenX_WhenY_ThenZ`); unit tests use `[UnitFact]`, functional tests use `[FunctionalFact]`.
- `Roles` enum values are the seeded `RoleId`s: `SystemAdmin = 1`, `ScopeAdmin = 2`, `User = 3`.

---

### Task 1: HardDeleteScopeCommand handler + scaffolding (unit-tested)

Builds the command, output, canonical message + status mapping, and the handler with its full unit-test suite. Deliverable: green unit tests for the main flow and the handler-level alternative flow (AF-05a), plus the explicit-delete cascade behavior.

**Files:**
- Create: `src/Application/ArturRios.Heimdall.Command/Input/HardDeleteScopeCommand.cs`
- Create: `src/Application/ArturRios.Heimdall.Command/Output/HardDeleteScopeCommandOutput.cs`
- Create: `src/Application/ArturRios.Heimdall.Command/Handlers/HardDeleteScopeCommandHandler.cs`
- Modify: `src/Application/ArturRios.Heimdall.Shared/Messages/ScopeMessages.cs`
- Modify: `src/Application/ArturRios.Heimdall.Shared/Messages/ScopeMessageMap.cs`
- Test: `tests/Application/ArturRios.Heimdall.Command.Tests/HardDeleteScopeCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `IAsyncReadOnlyRepository<T>` / `IAsyncRepository<T>` (from `ArturRios.Data.Relational.Core.Interfaces`); `DataOutput<T>` (`.New`, `.WithData`, `.WithMessage`, `.WithError`, `.WithErrors`, `.Success`, `.Data`, `.Errors`, `.Messages`); `ProcessOutput` returned by `DeleteAsync`/`DeleteRangeAsync` (`.Success`, `.Errors`); `IAsyncRepository<T>.DeleteAsync(T entity)` and `IAsyncRepository<T>.DeleteRangeAsync(IEnumerable<long> ids)`; `ICommandHandlerAsync<TCommand, TOutput>` (from `ArturRios.Mediator.Command.Interfaces`); entities `Scope`, `Person`, `GoogleUser`, `Application` (each `: Entity` with a `long Id`).
- Produces:
  - `HardDeleteScopeCommand : BaseCommand` with `Guid Id`.
  - `HardDeleteScopeCommandOutput : CommandOutput` with `Guid Id`, `int DeletedUserCount`, `int DeletedGoogleUserCount`, `int DeletedApplicationCount`.
  - `HardDeleteScopeCommandHandler : ICommandHandlerAsync<HardDeleteScopeCommand, HardDeleteScopeCommandOutput>` with constructor `(IAsyncReadOnlyRepository<Scope> scopeReader, IAsyncRepository<Scope> scopeWriter, IAsyncReadOnlyRepository<Person> personReader, IAsyncRepository<Person> personWriter, IAsyncReadOnlyRepository<GoogleUser> googleUserReader, IAsyncRepository<GoogleUser> googleUserWriter, IAsyncReadOnlyRepository<Application> applicationReader, IAsyncRepository<Application> applicationWriter)`.
  - `ScopeMessages.ScopeHardDeletedSuccessfully` mapped to 200 OK.

- [ ] **Step 1: Create the command**

Create `src/Application/ArturRios.Heimdall.Command/Input/HardDeleteScopeCommand.cs`:

```csharp
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to permanently (hard) delete a scope (UC-05). The scope is addressed by its
///     <c>PublicId</c> (GUID), bound from the route. Removing the scope also permanently removes its
///     Users, Google Users, applications, and its <c>SCOPE_OWNER</c>/<c>SCOPE_USER</c> join rows; the
///     owner person records are left intact.
/// </summary>
public class HardDeleteScopeCommand : BaseCommand
{
    /// <summary>Public identifier of the scope to hard-delete (bound from the route).</summary>
    public Guid Id { get; set; }
}
```

- [ ] **Step 2: Create the output**

Create `src/Application/ArturRios.Heimdall.Command/Output/HardDeleteScopeCommandOutput.cs`:

```csharp
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.HardDeleteScopeCommand" /> (UC-05). Reports the removed scope's
///     <c>PublicId</c> and the totals of its Users, Google Users, and applications — counted
///     regardless of their individual deletion state. Internal Ids never leave the data layer.
/// </summary>
public class HardDeleteScopeCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the hard-deleted scope.</summary>
    public Guid Id { get; set; }

    /// <summary>Total number of Users (via SCOPE_USER) that belonged to the scope.</summary>
    public int DeletedUserCount { get; set; }

    /// <summary>Total number of Google Users that belonged to the scope.</summary>
    public int DeletedGoogleUserCount { get; set; }

    /// <summary>Total number of applications that belonged to the scope.</summary>
    public int DeletedApplicationCount { get; set; }
}
```

- [ ] **Step 3: Add the canonical message and its status mapping**

In `src/Application/ArturRios.Heimdall.Shared/Messages/ScopeMessages.cs`, add after the `ScopeDeletedSuccessfully` constant (line ~17):

```csharp
    /// <summary>UC-05 success: the scope was permanently (hard) deleted.</summary>
    public const string ScopeHardDeletedSuccessfully = "Scope hard deleted successfully.";
```

In `src/Application/ArturRios.Heimdall.Shared/Messages/ScopeMessageMap.cs`, add an entry after the `ScopeDeletedSuccessfully` mapping (line ~19):

```csharp
        // UC-05 main flow — scope hard deleted.
        [ScopeMessages.ScopeHardDeletedSuccessfully] = HttpStatusCodes.Ok,
```

(`ScopeMessages.ScopeNotFound` → `HttpStatusCodes.NotFound` already exists and covers AF-05a — do not duplicate it.)

- [ ] **Step 4: Write the failing unit tests**

Create `tests/Application/ArturRios.Heimdall.Command.Tests/HardDeleteScopeCommandHandlerTests.cs`:

```csharp
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for HardDeleteScopeCommandHandler (UC-05).
// Cover the main flow, the explicit cascade to Users/Google Users/applications, and AF-05a (not
// found). Authorization (403/401) and the SCOPE_OWNER/SCOPE_USER join-row cascade are functional
// concerns (the fake repositories are not join-aware).
public class HardDeleteScopeCommandHandlerTests
{
    // One fake per aggregate; each is passed as BOTH the reader and the writer argument.
    private sealed record Fakes(
        AsyncFakeRepository<Scope> Scopes,
        AsyncFakeRepository<Person> Persons,
        AsyncFakeRepository<GoogleUser> GoogleUsers,
        AsyncFakeRepository<Application> Applications)
    {
        public HardDeleteScopeCommandHandler Handler() => new(
            Scopes, Scopes, Persons, Persons, GoogleUsers, GoogleUsers, Applications, Applications);
    }

    private static async Task<Fakes> EmptyFakes()
    {
        await Task.CompletedTask;
        return new Fakes(
            new AsyncFakeRepository<Scope>(),
            new AsyncFakeRepository<Person>(),
            new AsyncFakeRepository<GoogleUser>(),
            new AsyncFakeRepository<Application>());
    }

    private static async Task<Scope> SeedScopeAsync(Fakes fakes, bool isDeleted = false)
    {
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}", IsDeleted = isDeleted };
        await fakes.Scopes.CreateAsync(scope);
        return scope;
    }

    private static async Task SeedUserAsync(Fakes fakes, long scopeId, bool isDeleted = false)
    {
        // ScopeMembership is set at construction (the handler's query only reads ScopeMembership.ScopeId).
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            IsDeleted = isDeleted,
            ScopeMembership = new ScopeUser { ScopeId = scopeId }
        };
        await fakes.Persons.CreateAsync(person);
    }

    private static async Task SeedGoogleUserAsync(Fakes fakes, long scopeId, bool isDeleted = false)
    {
        await fakes.GoogleUsers.CreateAsync(new GoogleUser { PublicId = Guid.NewGuid(), ScopeId = scopeId, IsDeleted = isDeleted });
    }

    private static async Task SeedApplicationAsync(Fakes fakes, long scopeId, bool isDeleted = false)
    {
        await fakes.Applications.CreateAsync(new Application { PublicId = Guid.NewGuid(), ScopeId = scopeId, IsDeleted = isDeleted });
    }

    [UnitFact]
    public async Task GivenScopeWithNoMembers_WhenHandlingHardDeleteScope_ThenScopeIsRemovedWithZeroCounts()
    {
        // Given
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        var command = new HardDeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — output
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.Equal(scope.PublicId, output.Data!.Id);
        Assert.Equal(0, output.Data.DeletedUserCount);
        Assert.Equal(0, output.Data.DeletedGoogleUserCount);
        Assert.Equal(0, output.Data.DeletedApplicationCount);
        Assert.Contains(ScopeMessages.ScopeHardDeletedSuccessfully, output.Messages);

        // Then — the scope is gone from the store
        Assert.Empty((await fakes.Scopes.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenScopeWithMembers_WhenHandlingHardDeleteScope_ThenMembersAreRemovedAndCounted()
    {
        // Given a scope with two Users, one Google User, and one application
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        await SeedUserAsync(fakes, scope.Id);
        await SeedUserAsync(fakes, scope.Id);
        await SeedGoogleUserAsync(fakes, scope.Id);
        await SeedApplicationAsync(fakes, scope.Id);
        var command = new HardDeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — counts reflect the totals
        Assert.True(output.Success);
        Assert.Equal(2, output.Data!.DeletedUserCount);
        Assert.Equal(1, output.Data.DeletedGoogleUserCount);
        Assert.Equal(1, output.Data.DeletedApplicationCount);

        // Then — the scope and every member are removed from their stores
        Assert.Empty((await fakes.Scopes.GetAllAsync()).Data!);
        Assert.Empty((await fakes.Persons.GetAllAsync()).Data!);
        Assert.Empty((await fakes.GoogleUsers.GetAllAsync()).Data!);
        Assert.Empty((await fakes.Applications.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenScopeWithAlreadyDeletedMember_WhenHandlingHardDeleteScope_ThenMemberStillCountedAndRemoved()
    {
        // Given a scope whose single User is already individually logically deleted
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        await SeedUserAsync(fakes, scope.Id, isDeleted: true);
        var command = new HardDeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — the already-deleted User is still counted and still removed
        Assert.True(output.Success);
        Assert.Equal(1, output.Data!.DeletedUserCount);
        Assert.Empty((await fakes.Persons.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenAlreadyLogicallyDeletedScope_WhenHandlingHardDeleteScope_ThenScopeIsRemoved()
    {
        // Given a logically deleted scope that still has one application
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes, isDeleted: true);
        await SeedApplicationAsync(fakes, scope.Id, isDeleted: true);
        var command = new HardDeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — the scope is hard-deleted regardless of its logical-deletion state
        Assert.True(output.Success);
        Assert.Equal(scope.PublicId, output.Data!.Id);
        Assert.Equal(1, output.Data.DeletedApplicationCount);
        Assert.Empty((await fakes.Scopes.GetAllAsync()).Data!);
        Assert.Empty((await fakes.Applications.GetAllAsync()).Data!);
        Assert.Contains(ScopeMessages.ScopeHardDeletedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenMissingScope_WhenHandlingHardDeleteScope_ThenReturnsScopeNotFound()
    {
        // Given an empty store
        var fakes = await EmptyFakes();
        var command = new HardDeleteScopeCommand { Id = Guid.NewGuid() };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
    }
}
```

- [ ] **Step 5: Run the unit tests to verify they fail**

Run:

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "FullyQualifiedName~HardDeleteScopeCommandHandlerTests"
```

Expected: FAIL — build error, `HardDeleteScopeCommandHandler` does not exist.

- [ ] **Step 6: Implement the handler**

Create `src/Application/ArturRios.Heimdall.Command/Handlers/HardDeleteScopeCommandHandler.cs`:

```csharp
using ArturRios.Data.Relational.Core.Entities;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="HardDeleteScopeCommand" /> (UC-05): locates the scope (AF-05a), then
///     permanently deletes its Users (via <c>SCOPE_USER</c>), Google Users, and applications, and
///     finally the scope itself — whose <c>ON DELETE CASCADE</c> foreign keys remove the
///     <c>SCOPE_OWNER</c>/<c>SCOPE_USER</c> join rows. Owner person records (<c>ScopeAdmin</c>s) are
///     never removed. The response reports the totals of the scope's members, counted regardless of
///     their individual deletion state. All failures are returned as errors on the
///     <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
public class HardDeleteScopeCommandHandler(
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncRepository<Scope> scopeWriter,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Person> personWriter,
    IAsyncReadOnlyRepository<GoogleUser> googleUserReader,
    IAsyncRepository<GoogleUser> googleUserWriter,
    IAsyncReadOnlyRepository<Application> applicationReader,
    IAsyncRepository<Application> applicationWriter)
    : ICommandHandlerAsync<HardDeleteScopeCommand, HardDeleteScopeCommandOutput>
{
    public async Task<DataOutput<HardDeleteScopeCommandOutput?>> HandleAsync(HardDeleteScopeCommand command)
    {
        var output = DataOutput<HardDeleteScopeCommandOutput?>.New;

        // Step 2 (AF-05a): locate the scope in ANY deletion state — an already logically-deleted
        // scope can still be hard-deleted.
        var scope = await scopeReader.Query().FirstOrDefaultAsync(x => x.PublicId == command.Id);

        if (scope is null)
        {
            return output.WithError(ScopeMessages.ScopeNotFound);
        }

        // Step 3: the scope's members, counted regardless of individual deletion state.
        var users = await personReader.Query()
            .Where(p => p.ScopeMembership != null && p.ScopeMembership.ScopeId == scope.Id)
            .ToListAsync();
        var googleUsers = await googleUserReader.Query()
            .Where(g => g.ScopeId == scope.Id)
            .ToListAsync();
        var applications = await applicationReader.Query()
            .Where(a => a.ScopeId == scope.Id)
            .ToListAsync();

        // Step 4: delete the members explicitly, in an order that never violates a foreign key
        // (applications reference their owning person, so they go first).
        var deleteErrors = (await DeleteAllAsync(applications, applicationWriter))
            .Concat(await DeleteAllAsync(googleUsers, googleUserWriter))
            .Concat(await DeleteAllAsync(users, personWriter))
            .ToList();

        if (deleteErrors.Count > 0)
        {
            return output.WithErrors(deleteErrors);
        }

        // Step 5: delete the scope; its ON DELETE CASCADE foreign keys clear the SCOPE_OWNER and any
        // remaining SCOPE_USER join rows. Owner person records are untouched.
        var scopeDelete = await scopeWriter.DeleteAsync(scope);

        if (!scopeDelete.Success)
        {
            return output.WithErrors(scopeDelete.Errors);
        }

        // Step 6: return the scope id and the member totals.
        return output
            .WithData(new HardDeleteScopeCommandOutput
            {
                Id = scope.PublicId,
                DeletedUserCount = users.Count,
                DeletedGoogleUserCount = googleUsers.Count,
                DeletedApplicationCount = applications.Count
            })
            .WithMessage(ScopeMessages.ScopeHardDeletedSuccessfully);
    }

    /// <summary>
    ///     Permanently removes every entity in <paramref name="members" /> by internal Id, or does
    ///     nothing when the set is empty. Returns any persistence errors, or an empty sequence on
    ///     success / no-op.
    /// </summary>
    private static async Task<IEnumerable<string>> DeleteAllAsync<T>(
        IReadOnlyCollection<T> members,
        IAsyncRepository<T> writer) where T : Entity
    {
        if (members.Count == 0)
        {
            return [];
        }

        var result = await writer.DeleteRangeAsync(members.Select(member => member.Id));

        return result.Success ? [] : result.Errors;
    }
}
```

- [ ] **Step 7: Run the unit tests to verify they pass**

Run:

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "FullyQualifiedName~HardDeleteScopeCommandHandlerTests"
```

Expected: PASS — all five tests green.

- [ ] **Step 8: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Command/Input/HardDeleteScopeCommand.cs \
        src/Application/ArturRios.Heimdall.Command/Output/HardDeleteScopeCommandOutput.cs \
        src/Application/ArturRios.Heimdall.Command/Handlers/HardDeleteScopeCommandHandler.cs \
        src/Application/ArturRios.Heimdall.Shared/Messages/ScopeMessages.cs \
        src/Application/ArturRios.Heimdall.Shared/Messages/ScopeMessageMap.cs \
        tests/Application/ArturRios.Heimdall.Command.Tests/HardDeleteScopeCommandHandlerTests.cs
git commit -m "feat: add UC-05 hard delete scope command handler"
```

---

### Task 2: Controller endpoint + DI wiring (functional-tested)

Exposes `DELETE /api/scopes/{id}/hard` restricted to System Admins, registers the handler for DI, and covers the endpoint end-to-end (main flow with the full cascade, AF-05a, hard-deleting a logically-deleted scope, and authorization) against Testcontainers PostgreSQL. Deliverable: green functional tests.

**Files:**
- Modify: `src/Presentation/ArturRios.Heimdall.WebApi/Controllers/ScopeController.cs`
- Modify: `src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs`
- Test: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ScopeControllerHardDeleteTests.cs`

**Interfaces:**
- Consumes: `HardDeleteScopeCommand`, `HardDeleteScopeCommandOutput`, `ScopeMessageMap.StatusCodes`, `CommandMediator.ExecuteCommandAsync<HardDeleteScopeCommand, HardDeleteScopeCommandOutput>`, `ResponseResolver.Resolve`, `[RoleRequirement((int)Roles.SystemAdmin)]`, `ICommandHandlerAsync<HardDeleteScopeCommand, HardDeleteScopeCommandOutput>` → `HardDeleteScopeCommandHandler` (Task 1); test support `TestTokens.ForRole`, `PostgresFixture`, `WebApiTest<Program>.Authorize`, `Gateway.DeleteAsync<T>(url)`.
- Produces: HTTP endpoint `DELETE /api/scopes/{id:guid}/hard`.

- [ ] **Step 1: Write the failing functional tests**

Create `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ScopeControllerHardDeleteTests.cs`:

```csharp
using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

[Collection(nameof(FunctionalCollection))]
public class ScopeControllerHardDeleteTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string UniqueName() => $"scope-{Guid.NewGuid():N}";

    private async Task<Scope> SeedScopeAsync(bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = UniqueName(), IsDeleted = isDeleted };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedPersonAsync(Roles role)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Person",
            Email = $"person-{Guid.NewGuid():N}@test.local",
            RoleId = (long)role,
            EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    private async Task<Person> SeedOwnerAsync(Scope scope)
    {
        var owner = await SeedPersonAsync(Roles.ScopeAdmin);
        await using var context = db.CreateContext();
        context.ScopeOwners.Add(new ScopeOwner { ScopeId = scope.Id, PersonId = owner.Id });
        await context.SaveChangesAsync();
        return owner;
    }

    private async Task<Person> SeedScopeUserAsync(Scope scope)
    {
        var user = await SeedPersonAsync(Roles.User);
        await using var context = db.CreateContext();
        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = user.Id });
        await context.SaveChangesAsync();
        return user;
    }

    private async Task<GoogleUser> SeedGoogleUserAsync(Scope scope)
    {
        await using var context = db.CreateContext();
        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = Guid.NewGuid().ToString("N"),
            Name = "Google User",
            Email = $"google-{Guid.NewGuid():N}@test.local",
            ScopeId = scope.Id
        };
        context.GoogleUsers.Add(googleUser);
        await context.SaveChangesAsync();
        return googleUser;
    }

    private async Task<Application> SeedApplicationAsync(Scope scope, Person owner)
    {
        await using var context = db.CreateContext();
        var application = new Application
        {
            PublicId = Guid.NewGuid(),
            Name = $"app-{Guid.NewGuid():N}",
            ScopeId = scope.Id,
            OwnerId = owner.Id
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application;
    }

    [FunctionalFact]
    public async Task GivenSystemAdminAndScopeWithMembers_WhenHardDeleteScope_ThenScopeAndMembersAreRemovedButOwnerRemains()
    {
        // Given a scope with an owner, two Users, one Google User, and one application
        var scope = await SeedScopeAsync();
        var owner = await SeedOwnerAsync(scope);
        var user1 = await SeedScopeUserAsync(scope);
        var user2 = await SeedScopeUserAsync(scope);
        var googleUser = await SeedGoogleUserAsync(scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/hard");

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(scope.PublicId, response.Body?.Data?.Id);
        Assert.Equal(2, response.Body?.Data?.DeletedUserCount);
        Assert.Equal(1, response.Body?.Data?.DeletedGoogleUserCount);
        Assert.Equal(1, response.Body?.Data?.DeletedApplicationCount);

        // Then — database state: the scope, its members, and its join rows are gone
        await using var context = db.CreateContext();
        Assert.False(await context.Scopes.AsNoTracking().AnyAsync(x => x.Id == scope.Id));
        Assert.False(await context.Persons.AsNoTracking().AnyAsync(x => x.Id == user1.Id));
        Assert.False(await context.Persons.AsNoTracking().AnyAsync(x => x.Id == user2.Id));
        Assert.False(await context.GoogleUsers.AsNoTracking().AnyAsync(x => x.Id == googleUser.Id));
        Assert.False(await context.Applications.AsNoTracking().AnyAsync(x => x.Id == application.Id));
        Assert.False(await context.ScopeOwners.AsNoTracking().AnyAsync(x => x.ScopeId == scope.Id));
        Assert.False(await context.ScopeUsers.AsNoTracking().AnyAsync(x => x.ScopeId == scope.Id));
        // The owner (ScopeAdmin) person record itself is NOT removed.
        Assert.True(await context.Persons.AsNoTracking().AnyAsync(x => x.Id == owner.Id));
    }

    [FunctionalFact]
    public async Task GivenUnknownScopeId_WhenHardDeleteScope_ThenNotFound()
    {
        // Given
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteScopeCommandOutput?>>(
            $"/api/scopes/{Guid.NewGuid()}/hard");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedScope_WhenHardDeleteScope_ThenOkAndScopeRemoved()
    {
        // Given an already logically deleted scope with one application
        var scope = await SeedScopeAsync(isDeleted: true);
        var owner = await SeedOwnerAsync(scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/hard");

        // Then — success; the scope and its application are gone, the owner person remains
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.Data?.DeletedApplicationCount);

        await using var context = db.CreateContext();
        Assert.False(await context.Scopes.AsNoTracking().AnyAsync(x => x.Id == scope.Id));
        Assert.False(await context.Applications.AsNoTracking().AnyAsync(x => x.Id == application.Id));
        Assert.True(await context.Persons.AsNoTracking().AnyAsync(x => x.Id == owner.Id));
    }

    [FunctionalFact]
    public async Task GivenNonSystemAdmin_WhenHardDeleteScope_ThenForbidden()
    {
        // Given
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/hard");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenHardDeleteScope_ThenUnauthorized()
    {
        // Given a scope but no bearer token on the gateway
        var scope = await SeedScopeAsync();

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/hard");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run the functional tests to verify they fail**

Run:

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "FullyQualifiedName~ScopeControllerHardDeleteTests"
```

Expected: FAIL — the `/hard` route does not exist yet (the endpoint/DI is missing, so the request returns 404/405 and the main-flow assertions fail).

- [ ] **Step 3: Add the controller action**

In `src/Presentation/ArturRios.Heimdall.WebApi/Controllers/ScopeController.cs`, add after the `Delete` action (after line ~60, before `List`):

```csharp
    /// <summary>
    ///     Permanently (hard) deletes a scope, removing its Users, Google Users, applications, and
    ///     ownership/membership join rows (UC-05). Restricted to System Admins.
    /// </summary>
    [HttpDelete("{id:guid}/hard")]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<DataOutput<HardDeleteScopeCommandOutput?>>> HardDelete(Guid id)
    {
        var result = await commandMediator
            .ExecuteCommandAsync<HardDeleteScopeCommand, HardDeleteScopeCommandOutput>(
                new HardDeleteScopeCommand { Id = id });

        return ResponseResolver.Resolve(result, statusMap: ScopeMessageMap.StatusCodes);
    }
```

(`HardDeleteScopeCommand` and `HardDeleteScopeCommandOutput` are already covered by the existing `using ArturRios.Heimdall.Command.Input;` and `using ArturRios.Heimdall.Command.Output;` at the top of the file.)

- [ ] **Step 4: Register the handler for DI**

In `src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs`, add after the `DeleteScopeCommandHandler` registration (after line ~106):

```csharp
        Builder.Services
            .AddScoped<ICommandHandlerAsync<HardDeleteScopeCommand, HardDeleteScopeCommandOutput>, HardDeleteScopeCommandHandler>();
```

(No validator registration — `HardDeleteScopeCommand` has no validated body fields.)

- [ ] **Step 5: Run the functional tests to verify they pass**

Run:

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "FullyQualifiedName~ScopeControllerHardDeleteTests"
```

Expected: PASS — all five tests green.

- [ ] **Step 6: Run the full suite to confirm no regressions**

Run:

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional"
```

Expected: PASS — the whole suite is green.

- [ ] **Step 7: Commit**

```bash
git add src/Presentation/ArturRios.Heimdall.WebApi/Controllers/ScopeController.cs \
        src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs \
        tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ScopeControllerHardDeleteTests.cs
git commit -m "feat: expose UC-05 hard delete scope endpoint"
```

---

### Task 3: Update the UC-05 use-case spec text

Aligns the Use Case Specification Document's UC-05 section with §8 / NFR-14 (and this implementation) by naming Google Users in the postcondition and main flow. Documentation-only; no tests.

**Files:**
- Modify: `docs/requirements/Use Case Specification Document.md`

- [ ] **Step 1: Update the UC-05 postcondition and main flow**

In `docs/requirements/Use Case Specification Document.md`, in the UC-05 section:

Change the **Postconditions** cell from:

```
| **Postconditions** | Scope, its `SCOPE_OWNER`/`SCOPE_USER` rows, its Users, and its applications are permanently removed from the database. Scope Admin person records are not removed, since they may own other scopes |
```

to:

```
| **Postconditions** | Scope, its `SCOPE_OWNER`/`SCOPE_USER` rows, its Users, its Google Users, and its applications are permanently removed from the database. Scope Admin person records are not removed, since they may own other scopes |
```

Change main-flow step 3 from:

```
3. The system permanently deletes all Users belonging to the scope (via SCOPE_USER) and all applications in the scope.
```

to:

```
3. The system permanently deletes all Users belonging to the scope (via SCOPE_USER), all Google Users in the scope, and all applications in the scope.
```

- [ ] **Step 2: Commit**

```bash
git add "docs/requirements/Use Case Specification Document.md"
git commit -m "docs: note Google User cascade in UC-05 hard delete spec"
```

---

## Notes for the implementer

- **`AsyncFakeRepository<T>` assigns `Id` on `CreateAsync`.** In unit tests, create the scope first, read back its assigned `scope.Id`, then create members whose `ScopeId` / `ScopeMembership.ScopeId` equal it. One fake instance per entity type is passed as both the reader and the writer constructor argument.
- **`DeleteRangeAsync` takes internal `Id`s** (`IEnumerable<long>`), not entities — pass `members.Select(m => m.Id)`. `DeleteAsync` takes the entity itself. Both return a `ProcessOutput` with `.Success` / `.Errors`.
- **The join-row cascade is DB-only.** `SCOPE_OWNER` / `SCOPE_USER` have no repository (they are not `Entity` types), so their removal is asserted only in the functional test, against the real PostgreSQL `ON DELETE CASCADE`. Do not attempt to delete them from the handler.
- **No migration.** If a migration prompt or pending-migration error appears, stop — the change must not alter the schema.
- **Do not delete owner persons.** Only the `SCOPE_OWNER` join rows go (via the scope-delete cascade); the functional main-flow test asserts the owner person row still exists.
