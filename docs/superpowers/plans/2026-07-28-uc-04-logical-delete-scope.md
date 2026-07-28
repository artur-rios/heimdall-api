# UC-04: Logical Delete Scope Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a System Admin logically delete a scope via `DELETE /api/scopes/{id}`, cascading `IsDeleted = true` to the scope's Users, Google Users, and applications, and returning the member totals.

**Architecture:** Mirror the existing UC-01/UC-03 write flow — a `DeleteScopeCommand` dispatched through `CommandMediator` to a `DeleteScopeCommandHandler` that returns a `DataOutput<DeleteScopeCommandOutput?>`; a thin controller action; canonical messages mapped to HTTP status codes. The handler depends on read-only + writable repositories for `Scope`, `Person`, `GoogleUser`, and `Application`, sharing the scoped `AppDbContext`.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core (PostgreSQL), FluentValidation (not needed here), xUnit + `AsyncFakeRepository` (ArturRios.Util.Test 2.2.0) + Bogus for unit tests, Testcontainers PostgreSQL for functional tests.

## Global Constraints

- Handlers return `DataOutput<T>` and never throw; failures are added as errors using a canonical `ScopeMessages` constant (System Requirements / repo pattern).
- Public vs internal IDs: inputs/outputs/routes use `PublicId` (GUID); foreign keys and joins use internal `Id` (bigint). Never expose or accept internal `Id` (System Requirements §4.0 / NFR-15).
- No EF migration — this change only flips existing `IsDeleted` columns; do **not** generate or edit migrations.
- Count semantics: `DeletedUserCount` / `DeletedGoogleUserCount` / `DeletedApplicationCount` are the totals of the scope's members regardless of their individual `IsDeleted` state, computed identically in the main flow and AF-04b.
- Test naming is Given-When-Then (`GivenX_WhenY_ThenZ`); unit tests use `[UnitFact]`, functional tests use `[FunctionalFact]`.
- `Roles` enum values are the seeded `RoleId`s: `SystemAdmin = 1`, `ScopeAdmin = 2`, `User = 3`.

---

### Task 1: DeleteScopeCommand handler + scaffolding (unit-tested)

Builds the command, output, canonical message + status mapping, and the handler with its full unit-test suite. Deliverable: green unit tests for the main flow and every handler-level alternative flow (AF-04a, AF-04b, cascade behavior).

**Files:**
- Create: `src/Application/ArturRios.IdentityManager.Command/Input/DeleteScopeCommand.cs`
- Create: `src/Application/ArturRios.IdentityManager.Command/Output/DeleteScopeCommandOutput.cs`
- Create: `src/Application/ArturRios.IdentityManager.Command/Handlers/DeleteScopeCommandHandler.cs`
- Modify: `src/Application/ArturRios.IdentityManager.Shared/Messages/ScopeMessages.cs`
- Modify: `src/Application/ArturRios.IdentityManager.Shared/Messages/ScopeMessageMap.cs`
- Test: `tests/Application/ArturRios.IdentityManager.Command.Tests/DeleteScopeCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `IAsyncReadOnlyRepository<T>` / `IAsyncRepository<T>` (from `ArturRios.Data.Relational.Core.Interfaces`); `DataOutput<T>` (`.New`, `.WithData`, `.WithMessage`, `.WithError`, `.WithErrors`, `.Success`, `.Data`, `.Errors`, `.Messages`); `ProcessOutput` returned by `UpdateAsync`/`UpdateRangeAsync` (`.Success`, `.Errors`); `ICommandHandlerAsync<TCommand, TOutput>` (from `ArturRios.Mediator.Command.Interfaces`); entities `Scope`, `Person`, `GoogleUser`, `Application`.
- Produces:
  - `DeleteScopeCommand : BaseCommand` with `Guid Id`.
  - `DeleteScopeCommandOutput : CommandOutput` with `Guid Id`, `int DeletedUserCount`, `int DeletedGoogleUserCount`, `int DeletedApplicationCount`.
  - `DeleteScopeCommandHandler : ICommandHandlerAsync<DeleteScopeCommand, DeleteScopeCommandOutput>` with constructor `(IAsyncReadOnlyRepository<Scope> scopeReader, IAsyncRepository<Scope> scopeWriter, IAsyncReadOnlyRepository<Person> personReader, IAsyncRepository<Person> personWriter, IAsyncReadOnlyRepository<GoogleUser> googleUserReader, IAsyncRepository<GoogleUser> googleUserWriter, IAsyncReadOnlyRepository<Application> applicationReader, IAsyncRepository<Application> applicationWriter)`.
  - `ScopeMessages.ScopeDeletedSuccessfully` mapped to 200 OK.

- [ ] **Step 1: Create the command**

Create `src/Application/ArturRios.IdentityManager.Command/Input/DeleteScopeCommand.cs`:

```csharp
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to logically delete a scope (UC-04). The scope is addressed by its <c>PublicId</c>
///     (GUID), bound from the route. Setting the scope's <c>IsDeleted</c> flag cascades to its Users,
///     Google Users, and applications.
/// </summary>
public class DeleteScopeCommand : BaseCommand
{
    /// <summary>Public identifier of the scope to delete (bound from the route).</summary>
    public Guid Id { get; set; }
}
```

- [ ] **Step 2: Create the output**

Create `src/Application/ArturRios.IdentityManager.Command/Output/DeleteScopeCommandOutput.cs`:

```csharp
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Output;

/// <summary>
///     Result of <see cref="Input.DeleteScopeCommand" /> (UC-04). Reports the deleted scope's
///     <c>PublicId</c> and the totals of its Users, Google Users, and applications — counted
///     regardless of their individual deletion state. Internal Ids never leave the data layer.
/// </summary>
public class DeleteScopeCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the deleted scope.</summary>
    public Guid Id { get; set; }

    /// <summary>Total number of Users (via SCOPE_USER) belonging to the scope.</summary>
    public int DeletedUserCount { get; set; }

    /// <summary>Total number of Google Users belonging to the scope.</summary>
    public int DeletedGoogleUserCount { get; set; }

    /// <summary>Total number of applications belonging to the scope.</summary>
    public int DeletedApplicationCount { get; set; }
}
```

- [ ] **Step 3: Add the canonical message and its status mapping**

In `src/Application/ArturRios.IdentityManager.Shared/Messages/ScopeMessages.cs`, add after the `ScopeUpdatedSuccessfully` constant (line ~14):

```csharp
    /// <summary>UC-04 success: the scope was logically deleted (also used for the AF-04b idempotent no-op).</summary>
    public const string ScopeDeletedSuccessfully = "Scope deleted successfully.";
```

In `src/Application/ArturRios.IdentityManager.Shared/Messages/ScopeMessageMap.cs`, add an entry after the `ScopeUpdatedSuccessfully` mapping (line ~17):

```csharp
        // UC-04 main flow (and AF-04b idempotent) — scope deleted.
        [ScopeMessages.ScopeDeletedSuccessfully] = HttpStatusCodes.Ok,
```

(`ScopeMessages.ScopeNotFound` → `HttpStatusCodes.NotFound` already exists and covers AF-04a — do not duplicate it.)

- [ ] **Step 4: Write the failing unit tests**

Create `tests/Application/ArturRios.IdentityManager.Command.Tests/DeleteScopeCommandHandlerTests.cs`:

```csharp
using ArturRios.IdentityManager.Command.Handlers;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for DeleteScopeCommandHandler (UC-04).
// Cover the main flow, the cascade to Users/Google Users/applications, AF-04a (not found), and
// AF-04b (already deleted, idempotent). Authorization (403/401) is a functional concern.
public class DeleteScopeCommandHandlerTests
{
    // One fake per aggregate; each is passed as BOTH the reader and the writer argument.
    private sealed record Fakes(
        AsyncFakeRepository<Scope> Scopes,
        AsyncFakeRepository<Person> Persons,
        AsyncFakeRepository<GoogleUser> GoogleUsers,
        AsyncFakeRepository<Application> Applications)
    {
        public DeleteScopeCommandHandler Handler() => new(
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
        // ScopeMembership is set at construction (the handler's query only reads ScopeMembership.ScopeId),
        // so it is stored with the person regardless of how the fake handles the reference.
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
    public async Task GivenScopeWithNoMembers_WhenHandlingDeleteScope_ThenScopeIsDeletedWithZeroCounts()
    {
        // Given
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        var command = new DeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — output
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.Equal(scope.PublicId, output.Data!.Id);
        Assert.Equal(0, output.Data.DeletedUserCount);
        Assert.Equal(0, output.Data.DeletedGoogleUserCount);
        Assert.Equal(0, output.Data.DeletedApplicationCount);
        Assert.Contains(ScopeMessages.ScopeDeletedSuccessfully, output.Messages);

        // Then — the scope is flipped in the store
        var stored = (await fakes.Scopes.GetAllAsync()).Data!.Single();
        Assert.True(stored.IsDeleted);
    }

    [UnitFact]
    public async Task GivenScopeWithMembers_WhenHandlingDeleteScope_ThenMembersAreLogicallyDeletedAndCounted()
    {
        // Given a scope with two Users, one Google User, and one application
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        await SeedUserAsync(fakes, scope.Id);
        await SeedUserAsync(fakes, scope.Id);
        await SeedGoogleUserAsync(fakes, scope.Id);
        await SeedApplicationAsync(fakes, scope.Id);
        var command = new DeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — counts reflect the totals
        Assert.True(output.Success);
        Assert.Equal(2, output.Data!.DeletedUserCount);
        Assert.Equal(1, output.Data.DeletedGoogleUserCount);
        Assert.Equal(1, output.Data.DeletedApplicationCount);

        // Then — every member is flipped
        Assert.All((await fakes.Persons.GetAllAsync()).Data!, p => Assert.True(p.IsDeleted));
        Assert.All((await fakes.GoogleUsers.GetAllAsync()).Data!, g => Assert.True(g.IsDeleted));
        Assert.All((await fakes.Applications.GetAllAsync()).Data!, a => Assert.True(a.IsDeleted));
    }

    [UnitFact]
    public async Task GivenScopeWithAlreadyDeletedMember_WhenHandlingDeleteScope_ThenMemberStillCounted()
    {
        // Given a scope whose single User is already individually logically deleted
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        await SeedUserAsync(fakes, scope.Id, isDeleted: true);
        var command = new DeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — the already-deleted User is still part of the total
        Assert.True(output.Success);
        Assert.Equal(1, output.Data!.DeletedUserCount);
    }

    [UnitFact]
    public async Task GivenMissingScope_WhenHandlingDeleteScope_ThenReturnsScopeNotFound()
    {
        // Given an empty store
        var fakes = await EmptyFakes();
        var command = new DeleteScopeCommand { Id = Guid.NewGuid() };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenAlreadyDeletedScope_WhenHandlingDeleteScope_ThenSucceedsIdempotentlyWithTotals()
    {
        // Given a logically deleted scope that still has one application
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes, isDeleted: true);
        await SeedApplicationAsync(fakes, scope.Id, isDeleted: true);
        var command = new DeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — idempotent success, totals still reported
        Assert.True(output.Success);
        Assert.Equal(scope.PublicId, output.Data!.Id);
        Assert.Equal(1, output.Data.DeletedApplicationCount);
        Assert.Contains(ScopeMessages.ScopeDeletedSuccessfully, output.Messages);
    }
}
```

- [ ] **Step 5: Run the unit tests to verify they fail**

Run:

```bash
dotnet test src/ArturRios.IdentityManager.sln --filter "FullyQualifiedName~DeleteScopeCommandHandlerTests"
```

Expected: FAIL — build error, `DeleteScopeCommandHandler` does not exist.

- [ ] **Step 6: Implement the handler**

Create `src/Application/ArturRios.IdentityManager.Command/Handlers/DeleteScopeCommandHandler.cs`:

```csharp
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Command.Handlers;

/// <summary>
///     Handles <see cref="DeleteScopeCommand" /> (UC-04): locates the scope (AF-04a), then logically
///     deletes it and cascades <c>IsDeleted = true</c> to its Users (via <c>SCOPE_USER</c>), Google
///     Users, and applications. Owners (<c>SCOPE_OWNER</c>) are never modified. An already-deleted
///     scope is an idempotent no-op (AF-04b). The response reports the totals of the scope's members,
///     counted regardless of their individual deletion state. All failures are returned as errors on
///     the <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
public class DeleteScopeCommandHandler(
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncRepository<Scope> scopeWriter,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Person> personWriter,
    IAsyncReadOnlyRepository<GoogleUser> googleUserReader,
    IAsyncRepository<GoogleUser> googleUserWriter,
    IAsyncReadOnlyRepository<Application> applicationReader,
    IAsyncRepository<Application> applicationWriter)
    : ICommandHandlerAsync<DeleteScopeCommand, DeleteScopeCommandOutput>
{
    public async Task<DataOutput<DeleteScopeCommandOutput?>> HandleAsync(DeleteScopeCommand command)
    {
        var output = DataOutput<DeleteScopeCommandOutput?>.New;

        // Step 2 (AF-04a): locate the scope in ANY deletion state, so an already-deleted scope is
        // handled idempotently (AF-04b) rather than reported as not found.
        var scope = await scopeReader.Query().FirstOrDefaultAsync(x => x.PublicId == command.Id);

        if (scope is null)
        {
            return output.WithError(ScopeMessages.ScopeNotFound);
        }

        // Step 3: the scope's members, counted regardless of individual deletion state (both flows).
        var users = await personReader.Query()
            .Where(p => p.ScopeMembership != null && p.ScopeMembership.ScopeId == scope.Id)
            .ToListAsync();
        var googleUsers = await googleUserReader.Query()
            .Where(g => g.ScopeId == scope.Id)
            .ToListAsync();
        var applications = await applicationReader.Query()
            .Where(a => a.ScopeId == scope.Id)
            .ToListAsync();

        // AF-04b: an already-deleted scope is left untouched; the totals are still reported below.
        if (!scope.IsDeleted)
        {
            var now = DateTime.UtcNow;

            // Step 4: flip the scope itself.
            scope.IsDeleted = true;
            scope.UpdatedAt = now;

            var scopeUpdate = await scopeWriter.UpdateAsync(scope);

            if (!scopeUpdate.Success)
            {
                return output.WithErrors(scopeUpdate.Errors);
            }

            // Step 5: cascade to the members that are not already deleted.
            var cascadeErrors = (await CascadeAsync(users, p => p.IsDeleted,
                    p => { p.IsDeleted = true; p.UpdatedAt = now; }, personWriter))
                .Concat(await CascadeAsync(googleUsers, g => g.IsDeleted,
                    g => { g.IsDeleted = true; g.UpdatedAt = now; }, googleUserWriter))
                .Concat(await CascadeAsync(applications, a => a.IsDeleted,
                    a => { a.IsDeleted = true; a.UpdatedAt = now; }, applicationWriter))
                .ToList();

            if (cascadeErrors.Count > 0)
            {
                return output.WithErrors(cascadeErrors);
            }
        }

        // Step 6: return the scope id and the member totals.
        return output
            .WithData(new DeleteScopeCommandOutput
            {
                Id = scope.PublicId,
                DeletedUserCount = users.Count,
                DeletedGoogleUserCount = googleUsers.Count,
                DeletedApplicationCount = applications.Count
            })
            .WithMessage(ScopeMessages.ScopeDeletedSuccessfully);
    }

    /// <summary>
    ///     Flips <c>IsDeleted</c> (and <c>UpdatedAt</c>, via <paramref name="markDeleted" />) on the
    ///     members that are not already deleted, then persists them. Returns any persistence errors,
    ///     or an empty sequence when there is nothing to update.
    /// </summary>
    private static async Task<IEnumerable<string>> CascadeAsync<T>(
        IEnumerable<T> members,
        Func<T, bool> isDeleted,
        Action<T> markDeleted,
        IAsyncRepository<T> writer) where T : class
    {
        var pending = members.Where(member => !isDeleted(member)).ToList();

        if (pending.Count == 0)
        {
            return [];
        }

        foreach (var member in pending)
        {
            markDeleted(member);
        }

        var result = await writer.UpdateRangeAsync(pending);

        return result.Success ? [] : result.Errors;
    }
}
```

- [ ] **Step 7: Run the unit tests to verify they pass**

Run:

```bash
dotnet test src/ArturRios.IdentityManager.sln --filter "FullyQualifiedName~DeleteScopeCommandHandlerTests"
```

Expected: PASS — all five tests green.

- [ ] **Step 8: Commit**

```bash
git add src/Application/ArturRios.IdentityManager.Command/Input/DeleteScopeCommand.cs \
        src/Application/ArturRios.IdentityManager.Command/Output/DeleteScopeCommandOutput.cs \
        src/Application/ArturRios.IdentityManager.Command/Handlers/DeleteScopeCommandHandler.cs \
        src/Application/ArturRios.IdentityManager.Shared/Messages/ScopeMessages.cs \
        src/Application/ArturRios.IdentityManager.Shared/Messages/ScopeMessageMap.cs \
        tests/Application/ArturRios.IdentityManager.Command.Tests/DeleteScopeCommandHandlerTests.cs
git commit -m "feat: add UC-04 logical delete scope command handler"
```

---

### Task 2: Controller endpoint + DI wiring (functional-tested)

Exposes `DELETE /api/scopes/{id}` restricted to System Admins, registers the handler for DI, and covers the endpoint end-to-end (main flow with cascade, AF-04a, AF-04b, and authorization) against Testcontainers PostgreSQL. Deliverable: green functional tests.

**Files:**
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/Controllers/ScopeController.cs`
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs`
- Test: `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/ScopeControllerDeleteTests.cs`

**Interfaces:**
- Consumes: `DeleteScopeCommand`, `DeleteScopeCommandOutput`, `ScopeMessageMap.StatusCodes`, `CommandMediator.ExecuteCommandAsync<DeleteScopeCommand, DeleteScopeCommandOutput>`, `ResponseResolver.Resolve`, `[RoleRequirement((int)Roles.SystemAdmin)]`, `ICommandHandlerAsync<DeleteScopeCommand, DeleteScopeCommandOutput>` → `DeleteScopeCommandHandler` (Task 1); test support `TestTokens.ForRole`, `PostgresFixture`, `WebApiTest<Program>.Authorize`, `Gateway.DeleteAsync<T>(url)`.
- Produces: HTTP endpoint `DELETE /api/scopes/{id:guid}`.

- [ ] **Step 1: Write the failing functional tests**

Create `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/ScopeControllerDeleteTests.cs`:

```csharp
using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.WebApi.Tests;

[Collection(nameof(FunctionalCollection))]
public class ScopeControllerDeleteTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
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
    public async Task GivenSystemAdminAndScopeWithMembers_WhenDeleteScope_ThenScopeAndMembersAreLogicallyDeleted()
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
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}");

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(scope.PublicId, response.Body?.Data?.Id);
        Assert.Equal(2, response.Body?.Data?.DeletedUserCount);
        Assert.Equal(1, response.Body?.Data?.DeletedGoogleUserCount);
        Assert.Equal(1, response.Body?.Data?.DeletedApplicationCount);

        // Then — database state
        await using var context = db.CreateContext();
        Assert.True((await context.Scopes.AsNoTracking().FirstAsync(x => x.Id == scope.Id)).IsDeleted);
        Assert.True((await context.Persons.AsNoTracking().FirstAsync(x => x.Id == user1.Id)).IsDeleted);
        Assert.True((await context.Persons.AsNoTracking().FirstAsync(x => x.Id == user2.Id)).IsDeleted);
        Assert.True((await context.GoogleUsers.AsNoTracking().FirstAsync(x => x.Id == googleUser.Id)).IsDeleted);
        Assert.True((await context.Applications.AsNoTracking().FirstAsync(x => x.Id == application.Id)).IsDeleted);
        // The owner (ScopeAdmin) is not modified.
        Assert.False((await context.Persons.AsNoTracking().FirstAsync(x => x.Id == owner.Id)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenUnknownScopeId_WhenDeleteScope_ThenNotFound()
    {
        // Given
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopeCommandOutput?>>(
            $"/api/scopes/{Guid.NewGuid()}");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenAlreadyDeletedScope_WhenDeleteScope_ThenOkAndMembersUnchanged()
    {
        // Given an already logically deleted scope with one (already deleted) application
        var scope = await SeedScopeAsync(isDeleted: true);
        var owner = await SeedOwnerAsync(scope);
        var application = await SeedApplicationAsync(scope, owner);
        await using (var seedContext = db.CreateContext())
        {
            var app = await seedContext.Applications.FirstAsync(x => x.Id == application.Id);
            app.IsDeleted = true;
            await seedContext.SaveChangesAsync();
        }
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}");

        // Then — idempotent success, totals still reported
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.Data?.DeletedApplicationCount);
    }

    [FunctionalFact]
    public async Task GivenNonSystemAdmin_WhenDeleteScope_ThenForbidden()
    {
        // Given
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenDeleteScope_ThenUnauthorized()
    {
        // Given a scope but no bearer token on the gateway
        var scope = await SeedScopeAsync();

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run the functional tests to verify they fail**

Run:

```bash
dotnet test src/ArturRios.IdentityManager.sln --filter "FullyQualifiedName~ScopeControllerDeleteTests"
```

Expected: FAIL — the `DELETE` route does not exist yet (main-flow test build fails because `DeleteScopeCommandOutput` is referenced but the endpoint/DI is missing, and the request returns 404/405).

- [ ] **Step 3: Add the controller action**

In `src/Presentation/ArturRios.IdentityManager.WebApi/Controllers/ScopeController.cs`, add after the `Update` action (after line ~46, before `List`):

```csharp
    /// <summary>
    ///     Logically deletes a scope, cascading to its Users, Google Users, and applications (UC-04).
    ///     Restricted to System Admins.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<DataOutput<DeleteScopeCommandOutput?>>> Delete(Guid id)
    {
        var result = await commandMediator
            .ExecuteCommandAsync<DeleteScopeCommand, DeleteScopeCommandOutput>(new DeleteScopeCommand { Id = id });

        return ResponseResolver.Resolve(result, statusMap: ScopeMessageMap.StatusCodes);
    }
```

(`DeleteScopeCommand` and `DeleteScopeCommandOutput` are already covered by the existing `using ArturRios.IdentityManager.Command.Input;` and `using ArturRios.IdentityManager.Command.Output;` at the top of the file.)

- [ ] **Step 4: Register the handler for DI**

In `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs`, add after the `UpdateScopeCommandHandler` registration (after line ~104):

```csharp
        Builder.Services
            .AddScoped<ICommandHandlerAsync<DeleteScopeCommand, DeleteScopeCommandOutput>, DeleteScopeCommandHandler>();
```

(No validator registration — `DeleteScopeCommand` has no validated body fields.)

- [ ] **Step 5: Run the functional tests to verify they pass**

Run:

```bash
dotnet test src/ArturRios.IdentityManager.sln --filter "FullyQualifiedName~ScopeControllerDeleteTests"
```

Expected: PASS — all five tests green.

- [ ] **Step 6: Run the full suite to confirm no regressions**

Run:

```bash
dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"
dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Functional"
```

Expected: PASS — the whole suite is green.

- [ ] **Step 7: Commit**

```bash
git add src/Presentation/ArturRios.IdentityManager.WebApi/Controllers/ScopeController.cs \
        src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs \
        tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/ScopeControllerDeleteTests.cs
git commit -m "feat: expose UC-04 logical delete scope endpoint"
```

---

## Notes for the implementer

- **`AsyncFakeRepository<T>` assigns `Id` on `CreateAsync`.** In unit tests, create the scope first, read back its assigned `scope.Id`, then create members whose `ScopeId` / `ScopeMembership.ScopeId` equal it. One fake instance per entity type is passed as both the reader and the writer constructor argument.
- **`ProcessOutput.Errors` is `IEnumerable<string>`**, matching `DataOutput.WithErrors(...)`. The `CascadeAsync` helper returns `[]` (empty) on success or no-op and the writer's errors otherwise.
- **No migration.** If a migration prompt or pending-migration error appears, stop — the change must not alter the schema.
- **Do not modify owners.** `SCOPE_OWNER` rows and the ScopeAdmin persons behind them are never touched; the functional main-flow test asserts the owner's `IsDeleted` stays `false`.
```
