# UC-10 Hard Delete Person Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement UC-10 (Hard Delete Person) — one `DELETE /api/persons/{id}/hard` endpoint that permanently removes a person together with their owned applications, tokens, and scope join rows, refusing the request when it would strip a scope of its last owner or when the caller targets themselves.

**Architecture:** CQRS write flow mirroring UC-05 (Hard Delete Scope) for its explicit cascade and output counts, and UC-09 (Logical Delete Person) for its self-deletion guard and NFR-12 check. One command, handler, and output; the endpoint joins the existing `PersonController`. The handler returns `DataOutput<T>` and never throws. Actor identity arrives through the `IActorScoped` plumbing UC-07 moved into `Shared`.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core (PostgreSQL), ArturRios.Mediator / .Output / .Data.Relational.Core / .Util.WebApi; xUnit + Moq + Bogus + Testcontainers for tests. No FluentValidation here — there is no validator.

## Global Constraints

- **Design of record:** `docs/superpowers/specs/2026-07-29-uc-10-hard-delete-person-design.md`. Every decision below traces to it.
- **No schema change / no EF migration** — every foreign key pointing at `person` is already `ON DELETE CASCADE` from `InitialCreate` (`application.owner_id`, `password_reset_token.person_id`, `email_verification_token.person_id`, `scope_user.person_id`, `scope_owner.person_id`).
- **Identifiers:** routes, inputs and outputs use `PublicId` (GUID); joins and FKs use internal `Id` (bigint). Never expose or accept an internal `Id` (NFR-15).
- **Handlers return `DataOutput<T>` and never throw.** Failures are errors carrying a canonical `PersonMessages` value, which `ResponseResolver` maps through `PersonMessageMap.StatusCodes`.
- **Roles:** `SystemAdmin = 1`, `ScopeAdmin = 2`, `User = 3`; the seeder guarantees `Role.Id == (long)Roles`.
- **Acting user:** the auth middleware attaches `AuthenticatedUser(int Id, int Role)` to `HttpContext.Items["User"]`; the `Id` claim is the person's **internal** `Id`. `PersonController.ApplyActor` copies it onto any `IActorScoped`.
- **Lookup in any deletion state** — a logically deleted person must still be hard-deletable (Decision 1).
- **The NFR-12 guard is unconditional** — it applies even when the target is already logically deleted (Decision 4). This is deliberately the opposite of UC-09's AF-09b ordering.
- **Join rows are never deleted in code.** `ScopeUser` / `ScopeOwner` have composite keys and no surrogate `Id`, so no `IAsyncRepository<T>` can address them; deleting the person row clears them by database cascade.
- **Tests:** unit tests use one `AsyncFakeRepository<T>` per aggregate, each passed as both the reader and the writer argument; functional tests derive from `WebApiTest<Program>`, join `[Collection(nameof(FunctionalCollection))]`, authorize via `TestTokens`, and assert response **and** database state via `db.CreateContext()`. GWT naming, `// Given` / `// When` / `// Then`, `[UnitFact]` / `[FunctionalFact]`.
- **Run filters:** `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"` and `--filter "Category=Functional"`.
- **Commit style:** lowercase Conventional Commits subject, ≤50 chars, imperative; body wrapped at 72; trailer `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.

---

## File Structure

**New — production:**
- `src/Application/ArturRios.Heimdall.Command/Input/HardDeletePersonCommand.cs` — the route id plus the acting fields.
- `src/Application/ArturRios.Heimdall.Command/Output/HardDeletePersonCommandOutput.cs` — the removed person's `PublicId` and the two cascade counts.
- `src/Application/ArturRios.Heimdall.Command/Handlers/HardDeletePersonCommandHandler.cs` — the whole flow: lookup, both guards, the cascade, the delete.

**Modified — production:**
- `src/Application/ArturRios.Heimdall.Shared/Messages/PersonMessages.cs` — one new message.
- `src/Application/ArturRios.Heimdall.Shared/Messages/PersonMessageMap.cs` — its status code.
- `src/Presentation/ArturRios.Heimdall.WebApi/Controllers/PersonController.cs` — one DELETE action.
- `src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs` — handler registration.

**New — tests:**
- `tests/Application/ArturRios.Heimdall.Command.Tests/HardDeletePersonCommandHandlerTests.cs`
- `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/PersonControllerHardDeleteTests.cs`

**Modified — docs:**
- `docs/requirements/Use Case Specification Document.md` — UC-10 brought in line with the behaviour.
- `README.md` — UC-10 marked done in the status tracker (after the merge).

---

## Task 1: Command, output, and message

The inputs and vocabulary the handler needs. No handler yet, so nothing to unit-test in this task — the code must compile and the existing suite must stay green.

**Files:**
- Create: `src/Application/ArturRios.Heimdall.Command/Input/HardDeletePersonCommand.cs`
- Create: `src/Application/ArturRios.Heimdall.Command/Output/HardDeletePersonCommandOutput.cs`
- Modify: `src/Application/ArturRios.Heimdall.Shared/Messages/PersonMessages.cs`
- Modify: `src/Application/ArturRios.Heimdall.Shared/Messages/PersonMessageMap.cs`

**Interfaces:**
- Consumes: `BaseCommand` (`ArturRios.Mediator.Command`), `CommandOutput` (same namespace), `IActorScoped` (`ArturRios.Heimdall.Shared.Security` — `long ActingPersonId { get; set; }`, `int ActingRole { get; set; }`), `HttpStatusCodes` (`ArturRios.Util.Http`).
- Produces: `HardDeletePersonCommand { Guid Id; long ActingPersonId; int ActingRole }`, `HardDeletePersonCommandOutput { Guid Id; int DeletedApplicationCount; int DeletedTokenCount }`, and `PersonMessages.PersonHardDeletedSuccessfully`.

- [ ] **Step 1: Create `HardDeletePersonCommand`**

```csharp
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to permanently (hard) delete a person (UC-10). The person is addressed by
///     <see cref="Id" />, bound from the route. Removing the person also permanently removes the
///     applications they own, their password reset and email verification tokens, and their
///     <c>SCOPE_USER</c>/<c>SCOPE_OWNER</c> join rows. <see cref="ActingPersonId" /> is set by the
///     controller from the authenticated caller and is never bound from the request; it exists so the
///     handler can refuse a self-deletion (AF-10c).
/// </summary>
public class HardDeletePersonCommand : BaseCommand, IActorScoped
{
    /// <summary>Public identifier of the person to hard-delete (bound from the route).</summary>
    public Guid Id { get; set; }

    public long ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
```

- [ ] **Step 2: Create `HardDeletePersonCommandOutput`**

```csharp
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.HardDeletePersonCommand" /> (UC-10). Reports the removed person's
///     <c>PublicId</c> and the totals of the applications and tokens removed with them — counted
///     regardless of their individual deletion state. Internal Ids never leave the data layer.
/// </summary>
public class HardDeletePersonCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the hard-deleted person.</summary>
    public Guid Id { get; set; }

    /// <summary>Total number of applications the person owned.</summary>
    public int DeletedApplicationCount { get; set; }

    /// <summary>Total number of password reset and email verification tokens issued for the person.</summary>
    public int DeletedTokenCount { get; set; }
}
```

- [ ] **Step 3: Add the message to `PersonMessages`**

Append after the existing `CannotDeleteSelf` declaration, keeping the file's XML-doc style:

```csharp
    /// <summary>UC-10 success: the person was permanently (hard) deleted.</summary>
    public const string PersonHardDeletedSuccessfully = "Person hard deleted successfully.";
```

- [ ] **Step 4: Map its status code in `PersonMessageMap`**

Append inside the dictionary initializer, after the `CannotDeleteSelf` entry (note the comma that must be added to that line):

```csharp
        [PersonMessages.CannotDeleteSelf] = HttpStatusCodes.Forbidden,
        // UC-10 main flow — person hard deleted. AF-10a reuses PersonNotFound (404), AF-10b reuses
        // ScopeWouldLoseLastOwner (409), and AF-10c reuses CannotDeleteSelf (403).
        [PersonMessages.PersonHardDeletedSuccessfully] = HttpStatusCodes.Ok
```

Also update the class XML doc so it reads "following the UC-06, UC-07, UC-08, UC-09 and UC-10 flows."

- [ ] **Step 5: Verify the build and the existing suite**

Run: `dotnet build src/ArturRios.Heimdall.sln`
Expected: `Build succeeded`, 0 errors.

Run: `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"`
Expected: all tests pass (no new tests yet).

- [ ] **Step 6: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Command/Input/HardDeletePersonCommand.cs src/Application/ArturRios.Heimdall.Command/Output/HardDeletePersonCommandOutput.cs src/Application/ArturRios.Heimdall.Shared/Messages/PersonMessages.cs src/Application/ArturRios.Heimdall.Shared/Messages/PersonMessageMap.cs
git commit -m "feat: add hard delete person command (UC-10)"
```

---

## Task 2: Handler (test-first)

**Files:**
- Create: `tests/Application/ArturRios.Heimdall.Command.Tests/HardDeletePersonCommandHandlerTests.cs`
- Create: `src/Application/ArturRios.Heimdall.Command/Handlers/HardDeletePersonCommandHandler.cs`

**Interfaces:**
- Consumes: `HardDeletePersonCommand`, `HardDeletePersonCommandOutput`, `PersonMessages.{PersonHardDeletedSuccessfully, PersonNotFound, ScopeWouldLoseLastOwner, CannotDeleteSelf}` from Task 1; `IAsyncReadOnlyRepository<T>.Query()` and `IAsyncRepository<T>.DeleteAsync(T)` / `.DeleteRangeAsync(IEnumerable<long>)` from `ArturRios.Data.Relational.Core.Interfaces`; `Entity` (base class carrying `long Id`) from `ArturRios.Data.Relational.Core.Entities`.
- Produces: `HardDeletePersonCommandHandler` with constructor
  `(IAsyncReadOnlyRepository<Person> personReader, IAsyncRepository<Person> personWriter, IAsyncReadOnlyRepository<Application> applicationReader, IAsyncRepository<Application> applicationWriter, IAsyncReadOnlyRepository<PasswordResetToken> passwordResetTokenReader, IAsyncRepository<PasswordResetToken> passwordResetTokenWriter, IAsyncReadOnlyRepository<EmailVerificationToken> emailVerificationTokenReader, IAsyncRepository<EmailVerificationToken> emailVerificationTokenWriter)`
  implementing `ICommandHandlerAsync<HardDeletePersonCommand, HardDeletePersonCommandOutput>`.

- [ ] **Step 1: Write the failing unit tests**

Create the file with the fixture helpers plus all nine tests. The `Fakes` record mirrors `HardDeleteScopeCommandHandlerTests`: one fake per aggregate, each passed as both reader and writer.

```csharp
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for HardDeletePersonCommandHandler (UC-10). Cover the main flow with and without
// dependents, on an already logically deleted person, and on a co-owned ScopeAdmin; AF-10a (not
// found), AF-10b (the NFR-12 last-owner guard, including the already-deleted target of Decision 4),
// and AF-10c (self-deletion).
//
// The [RoleRequirement] gate that keeps a ScopeAdmin and a User out of the endpoint is a Presentation
// concern and is asserted in PersonControllerHardDeleteTests (Testing Specification §6.4). So is the
// SCOPE_USER/SCOPE_OWNER cascade: the fakes are not foreign-key aware.
public class HardDeletePersonCommandHandlerTests
{
    private sealed record Fakes(
        AsyncFakeRepository<Person> Persons,
        AsyncFakeRepository<Application> Applications,
        AsyncFakeRepository<PasswordResetToken> PasswordResetTokens,
        AsyncFakeRepository<EmailVerificationToken> EmailVerificationTokens)
    {
        public HardDeletePersonCommandHandler Handler() => new(
            Persons, Persons,
            Applications, Applications,
            PasswordResetTokens, PasswordResetTokens,
            EmailVerificationTokens, EmailVerificationTokens);
    }

    private static Fakes EmptyFakes() => new(
        new AsyncFakeRepository<Person>(),
        new AsyncFakeRepository<Application>(),
        new AsyncFakeRepository<PasswordResetToken>(),
        new AsyncFakeRepository<EmailVerificationToken>());

    private static Scope Scope(long id) => new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}" };

    private static async Task<Person> SeedUserAsync(Fakes fakes, Scope scope, bool isDeleted = false)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Member",
            Email = $"user-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.User,
            IsDeleted = isDeleted,
            ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope }
        };
        await fakes.Persons.CreateAsync(person);

        return person;
    }

    private static async Task<Person> SeedScopeAdminAsync(Fakes fakes, bool isDeleted = false, params Scope[] owned)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Admin",
            Email = $"admin-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.ScopeAdmin,
            IsDeleted = isDeleted,
            ScopeOwnerships = owned.Select(scope => new ScopeOwner { ScopeId = scope.Id, Scope = scope }).ToList()
        };
        await fakes.Persons.CreateAsync(person);

        return person;
    }

    private static async Task<Application> SeedApplicationAsync(Fakes fakes, Person owner, bool isDeleted = false)
    {
        var application = new Application
        {
            PublicId = Guid.NewGuid(),
            Name = $"app-{Guid.NewGuid():N}",
            ScopeId = 1,
            OwnerId = owner.Id,
            IsDeleted = isDeleted
        };
        await fakes.Applications.CreateAsync(application);

        return application;
    }

    private static async Task SeedTokensAsync(Fakes fakes, Person person)
    {
        await fakes.PasswordResetTokens.CreateAsync(new PasswordResetToken
        {
            PersonId = person.Id, Token = Guid.NewGuid().ToString("N"), ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await fakes.EmailVerificationTokens.CreateAsync(new EmailVerificationToken
        {
            PersonId = person.Id, Token = Guid.NewGuid().ToString("N"), ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
    }

    private static HardDeletePersonCommand CommandFor(Person target, long actingPersonId = 99) => new()
    {
        Id = target.PublicId,
        ActingRole = (int)Roles.SystemAdmin,
        ActingPersonId = actingPersonId
    };

    [UnitFact]
    public async Task GivenPersonWithDependents_WhenHandlingHardDeletePerson_ThenPersonAndDependentsAreRemoved()
    {
        // Given a User owning one application and holding one token of each kind
        var fakes = EmptyFakes();
        var target = await SeedUserAsync(fakes, Scope(1));
        await SeedApplicationAsync(fakes, target);
        await SeedTokensAsync(fakes, target);
        var command = CommandFor(target);

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — output
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.Equal(target.PublicId, output.Data!.Id);
        Assert.Equal(1, output.Data.DeletedApplicationCount);
        Assert.Equal(2, output.Data.DeletedTokenCount);
        Assert.Contains(PersonMessages.PersonHardDeletedSuccessfully, output.Messages);

        // Then — every store is empty
        Assert.Empty((await fakes.Persons.GetAllAsync()).Data!);
        Assert.Empty((await fakes.Applications.GetAllAsync()).Data!);
        Assert.Empty((await fakes.PasswordResetTokens.GetAllAsync()).Data!);
        Assert.Empty((await fakes.EmailVerificationTokens.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenPersonWithNoDependents_WhenHandlingHardDeletePerson_ThenPersonIsRemovedWithZeroCounts()
    {
        // Given
        var fakes = EmptyFakes();
        var target = await SeedUserAsync(fakes, Scope(1));
        var command = CommandFor(target);

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal(0, output.Data!.DeletedApplicationCount);
        Assert.Equal(0, output.Data.DeletedTokenCount);
        Assert.Empty((await fakes.Persons.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedPerson_WhenHandlingHardDeletePerson_ThenPersonIsRemoved()
    {
        // Given a person already soft-deleted: hard deletion works in any deletion state (Decision 1)
        var fakes = EmptyFakes();
        var target = await SeedUserAsync(fakes, Scope(1), isDeleted: true);
        await SeedApplicationAsync(fakes, target, isDeleted: true);
        var command = CommandFor(target);

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — the already-deleted application is still counted and still removed
        Assert.True(output.Success);
        Assert.Equal(1, output.Data!.DeletedApplicationCount);
        Assert.Empty((await fakes.Persons.GetAllAsync()).Data!);
        Assert.Empty((await fakes.Applications.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenScopeAdminWithCoOwnedScopes_WhenHandlingHardDeletePerson_ThenPersonIsRemoved()
    {
        // Given a ScopeAdmin whose only owned scope has another owner
        var fakes = EmptyFakes();
        var scope = Scope(1);
        var target = await SeedScopeAdminAsync(fakes, owned: scope);
        var coOwner = await SeedScopeAdminAsync(fakes, owned: scope);
        var command = CommandFor(target);

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — the target is gone, the co-owner remains
        Assert.True(output.Success);
        var remaining = (await fakes.Persons.GetAllAsync()).Data!;
        Assert.Single(remaining);
        Assert.Equal(coOwner.PublicId, remaining.First().PublicId);
    }

    [UnitFact]
    public async Task GivenAnotherPersonsDependents_WhenHandlingHardDeletePerson_ThenTheyAreLeftAlone()
    {
        // Given two Users, each owning an application and holding tokens
        var fakes = EmptyFakes();
        var scope = Scope(1);
        var target = await SeedUserAsync(fakes, scope);
        var bystander = await SeedUserAsync(fakes, scope);
        await SeedApplicationAsync(fakes, target);
        var bystanderApplication = await SeedApplicationAsync(fakes, bystander);
        await SeedTokensAsync(fakes, target);
        await SeedTokensAsync(fakes, bystander);
        var command = CommandFor(target);

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — only the target's rows are counted and removed
        Assert.True(output.Success);
        Assert.Equal(1, output.Data!.DeletedApplicationCount);
        Assert.Equal(2, output.Data.DeletedTokenCount);

        var remainingApplications = (await fakes.Applications.GetAllAsync()).Data!;
        Assert.Single(remainingApplications);
        Assert.Equal(bystanderApplication.PublicId, remainingApplications.First().PublicId);
        Assert.Single((await fakes.PasswordResetTokens.GetAllAsync()).Data!);
        Assert.Single((await fakes.EmailVerificationTokens.GetAllAsync()).Data!);
        Assert.Single((await fakes.Persons.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenPersonDoesNotExist_WhenHandlingHardDeletePerson_ThenReturnsPersonNotFoundError()
    {
        // Given — AF-10a
        var fakes = EmptyFakes();
        var command = new HardDeletePersonCommand
        {
            Id = Guid.NewGuid(), ActingRole = (int)Roles.SystemAdmin, ActingPersonId = 99
        };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenSoleOwnerScopeAdmin_WhenHandlingHardDeletePerson_ThenReturnsScopeWouldLoseLastOwnerError()
    {
        // Given — AF-10b: nobody else owns the scope
        var fakes = EmptyFakes();
        var target = await SeedScopeAdminAsync(fakes, owned: Scope(1));
        await SeedScopeAdminAsync(fakes, owned: Scope(2));
        await SeedApplicationAsync(fakes, target);
        var command = CommandFor(target);

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — refused, and nothing was removed
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeWouldLoseLastOwner, output.Errors);
        Assert.Equal(2, (await fakes.Persons.GetAllAsync()).Data!.Count());
        Assert.Single((await fakes.Applications.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedSoleOwner_WhenHandlingHardDeletePerson_ThenStillReturnsScopeWouldLoseLastOwnerError()
    {
        // Given — Decision 4: the guard applies even to an already soft-deleted sole owner, unlike
        // UC-09, where the idempotent AF-09b wins over it
        var fakes = EmptyFakes();
        var target = await SeedScopeAdminAsync(fakes, isDeleted: true, owned: Scope(1));
        var command = CommandFor(target);

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeWouldLoseLastOwner, output.Errors);
        Assert.Single((await fakes.Persons.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenActorTargetingThemselves_WhenHandlingHardDeletePerson_ThenReturnsCannotDeleteSelfError()
    {
        // Given — AF-10c, even for a System Admin
        var fakes = EmptyFakes();
        var target = await SeedUserAsync(fakes, Scope(1));
        target.RoleId = (long)Roles.SystemAdmin;
        var command = CommandFor(target, actingPersonId: target.Id);

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.CannotDeleteSelf, output.Errors);
        Assert.Single((await fakes.Persons.GetAllAsync()).Data!);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"`
Expected: compilation FAILS with `CS0246` — the type `HardDeletePersonCommandHandler` could not be found.

- [ ] **Step 3: Implement `HardDeletePersonCommandHandler`**

```csharp
using ArturRios.Data.Relational.Core.Entities;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="HardDeletePersonCommand" /> (UC-10): locates the person in any deletion state
///     (AF-10a), refuses a self-deletion (AF-10c), refuses to strip a scope of its last owner (AF-10b,
///     NFR-12), then permanently deletes the applications the person owns (NFR-11) and their password
///     reset and email verification tokens, and finally the person — whose <c>ON DELETE CASCADE</c>
///     foreign keys remove the <c>SCOPE_USER</c>/<c>SCOPE_OWNER</c> join rows. The response reports the
///     totals of the removed dependents, counted regardless of their individual deletion state. All
///     failures are returned as errors on the <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
public class HardDeletePersonCommandHandler(
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Person> personWriter,
    IAsyncReadOnlyRepository<Application> applicationReader,
    IAsyncRepository<Application> applicationWriter,
    IAsyncReadOnlyRepository<PasswordResetToken> passwordResetTokenReader,
    IAsyncRepository<PasswordResetToken> passwordResetTokenWriter,
    IAsyncReadOnlyRepository<EmailVerificationToken> emailVerificationTokenReader,
    IAsyncRepository<EmailVerificationToken> emailVerificationTokenWriter)
    : ICommandHandlerAsync<HardDeletePersonCommand, HardDeletePersonCommandOutput>
{
    public async Task<DataOutput<HardDeletePersonCommandOutput?>> HandleAsync(HardDeletePersonCommand command)
    {
        var output = DataOutput<HardDeletePersonCommandOutput?>.New;

        // AF-10a: the lookup omits an !IsDeleted filter — a logically deleted person is exactly what a
        // cleanup pass starts from, so it must still be hard-deletable. ScopeOwnerships is needed by
        // the last-owner guard below.
        var person = await personReader.Query()
            .Include(x => x.ScopeOwnerships)
            .FirstOrDefaultAsync(x => x.PublicId == command.Id);

        if (person is null)
        {
            return output.WithError(PersonMessages.PersonNotFound);
        }

        // AF-10c: nobody hard-deletes their own record, System Admin included, so one call cannot
        // permanently destroy the caller's own account. Checked before the last-owner guard, so a
        // caller targeting themselves gets the reason that applies to them.
        if (command.ActingPersonId == person.Id)
        {
            return output.WithError(PersonMessages.CannotDeleteSelf);
        }

        // UC-10 step 2 (AF-10b, NFR-12). Unlike UC-09, this runs regardless of the person's own
        // deletion state: NFR-12 names hard-deleting the last owning person explicitly, and the guard
        // keeps every scope row backed by at least one SCOPE_OWNER row.
        if (await WouldStripLastOwnerAsync(person))
        {
            return output.WithError(PersonMessages.ScopeWouldLoseLastOwner);
        }

        // UC-10 steps 3–4: the dependents, counted regardless of individual deletion state.
        var applications = await applicationReader.Query()
            .Where(a => a.OwnerId == person.Id)
            .ToListAsync();
        var passwordResetTokens = await passwordResetTokenReader.Query()
            .Where(t => t.PersonId == person.Id)
            .ToListAsync();
        var emailVerificationTokens = await emailVerificationTokenReader.Query()
            .Where(t => t.PersonId == person.Id)
            .ToListAsync();

        // Applications and tokens reference the person, so they go first and no foreign key is ever
        // violated.
        var deleteErrors = (await DeleteAllAsync(applications, applicationWriter))
            .Concat(await DeleteAllAsync(passwordResetTokens, passwordResetTokenWriter))
            .Concat(await DeleteAllAsync(emailVerificationTokens, emailVerificationTokenWriter))
            .ToList();

        if (deleteErrors.Count > 0)
        {
            return output.WithErrors(deleteErrors);
        }

        // UC-10 steps 5–6: delete the person; its ON DELETE CASCADE foreign keys clear the SCOPE_USER
        // or SCOPE_OWNER join rows.
        var personDelete = await personWriter.DeleteAsync(person);

        if (!personDelete.Success)
        {
            return output.WithErrors(personDelete.Errors);
        }

        // UC-10 step 7.
        return output
            .WithData(new HardDeletePersonCommandOutput
            {
                Id = person.PublicId,
                DeletedApplicationCount = applications.Count,
                DeletedTokenCount = passwordResetTokens.Count + emailVerificationTokens.Count
            })
            .WithMessage(PersonMessages.PersonHardDeletedSuccessfully);
    }

    /// <summary>
    ///     NFR-12. Gathers the scopes somebody *other* than this person owns and reports whether any
    ///     scope this person owns is missing from them — the same guard
    ///     <see cref="DeletePersonCommandHandler" /> and <see cref="UpdatePersonCommandHandler" />
    ///     apply. Persons already logically deleted are excluded, since they no longer keep a scope
    ///     owned.
    /// </summary>
    private async Task<bool> WouldStripLastOwnerAsync(Person person)
    {
        if (person.RoleId != (long)Roles.ScopeAdmin || person.ScopeOwnerships.Count == 0)
        {
            return false;
        }

        var ownedScopeIds = person.ScopeOwnerships.Select(ownership => ownership.ScopeId).ToList();

        var coOwnedScopeIds = await personReader.Query()
            .Where(other => other.Id != person.Id && !other.IsDeleted)
            .SelectMany(other => other.ScopeOwnerships.Select(ownership => ownership.ScopeId))
            .Distinct()
            .ToListAsync();

        return ownedScopeIds.Any(scopeId => !coOwnedScopeIds.Contains(scopeId));
    }

    /// <summary>
    ///     Permanently removes every entity in <paramref name="dependents" /> by internal Id, or does
    ///     nothing when the set is empty. Returns any persistence errors, or an empty sequence on
    ///     success / no-op.
    /// </summary>
    private static async Task<IEnumerable<string>> DeleteAllAsync<T>(
        IReadOnlyCollection<T> dependents,
        IAsyncRepository<T> writer) where T : Entity
    {
        if (dependents.Count == 0)
        {
            return [];
        }

        var result = await writer.DeleteRangeAsync(dependents.Select(dependent => dependent.Id));

        return result.Success ? [] : result.Errors;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"`
Expected: PASS — every existing unit test plus the nine new ones.

- [ ] **Step 5: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Command/Handlers/HardDeletePersonCommandHandler.cs tests/Application/ArturRios.Heimdall.Command.Tests/HardDeletePersonCommandHandlerTests.cs
git commit -m "feat: add hard delete person handler (UC-10)"
```

---

## Task 3: Endpoint and wiring

**Files:**
- Modify: `src/Presentation/ArturRios.Heimdall.WebApi/Controllers/PersonController.cs`
- Modify: `src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs`

**Interfaces:**
- Consumes: `HardDeletePersonCommand` / `HardDeletePersonCommandOutput` (Task 1), `HardDeletePersonCommandHandler` (Task 2), the controller's existing private `ApplyActor(IActorScoped)`.
- Produces: the route `DELETE /api/persons/{id}/hard`, which Task 4's functional tests call.

- [ ] **Step 1: Add the controller action**

Insert immediately after the existing `Delete` action in `PersonController`:

```csharp
    /// <summary>
    ///     Permanently (hard) deletes a person, removing the applications they own, their password
    ///     reset and email verification tokens, and their scope membership/ownership rows (UC-10,
    ///     FR-PE-07). Restricted to System Admins; the self-deletion refusal (AF-10c) and the
    ///     last-owner guard (AF-10b) are enforced by the handler.
    /// </summary>
    [HttpDelete("persons/{id:guid}/hard")]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<DataOutput<HardDeletePersonCommandOutput?>>> HardDelete(Guid id)
    {
        var command = new HardDeletePersonCommand { Id = id };
        ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<HardDeletePersonCommand, HardDeletePersonCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }
```

- [ ] **Step 2: Register the handler in `Startup.AddDependencies`**

Insert immediately after the `DeletePersonCommand` registration:

```csharp
        Builder.Services
            .AddScoped<ICommandHandlerAsync<HardDeletePersonCommand, HardDeletePersonCommandOutput>, HardDeletePersonCommandHandler>();
```

- [ ] **Step 3: Verify the build and the unit suite**

Run: `dotnet build src/ArturRios.Heimdall.sln`
Expected: `Build succeeded`, 0 errors.

Run: `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"`
Expected: PASS, same count as at the end of Task 2.

- [ ] **Step 4: Commit**

```bash
git add src/Presentation/ArturRios.Heimdall.WebApi/Controllers/PersonController.cs src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs
git commit -m "feat: expose hard delete person endpoint (UC-10)"
```

---

## Task 4: Functional tests

**Files:**
- Create: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/PersonControllerHardDeleteTests.cs`

**Interfaces:**
- Consumes: the route from Task 3, `HardDeletePersonCommandOutput` from Task 1, and the existing test support — `PostgresFixture.CreateContext()`, `FunctionalCollection`, `TestTokens.ForRole(int)` / `TestTokens.For(int id, int role)`, `WebApiTest<Program>`'s `Gateway` and `Authorize`.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Write the functional tests**

Seeding helpers follow `PersonControllerDeleteTests` and `ScopeControllerHardDeleteTests`. `TestTokens.ForRole` mints a token for person id `1`, which never collides with a freshly seeded person.

```csharp
using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for DELETE /api/persons/{id}/hard (UC-10): the main flow for a User with every kind
// of dependent and for a co-owned ScopeAdmin, an already logically deleted person, AF-10a (404),
// AF-10b (409, NFR-12), AF-10c (403, self-deletion), plus the [RoleRequirement] gate (403) and the
// unauthenticated flow (401). Asserts response and database state, including the join-row cascade the
// unit tests cannot observe.
[Collection(nameof(FunctionalCollection))]
public class PersonControllerHardDeleteTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

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
            Email = UniqueEmail("user"),
            RoleId = (long)Roles.User,
            EmailVerified = true,
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
            Email = UniqueEmail("admin"),
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

    private async Task SeedTokensAsync(Person person)
    {
        await using var context = db.CreateContext();
        context.PasswordResetTokens.Add(new PasswordResetToken
        {
            PersonId = person.Id, Token = Guid.NewGuid().ToString("N"), ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        context.EmailVerificationTokens.Add(new EmailVerificationToken
        {
            PersonId = person.Id, Token = Guid.NewGuid().ToString("N"), ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await context.SaveChangesAsync();
    }

    private async Task<bool> PersonExistsAsync(Person person)
    {
        await using var context = db.CreateContext();
        return await context.Persons.AsNoTracking().AnyAsync(p => p.Id == person.Id);
    }

    [FunctionalFact]
    public async Task GivenSystemAdminAndPersonWithDependents_WhenHardDeletePerson_ThenPersonAndDependentsAreRemoved()
    {
        // Given a User owning an application, holding both token kinds, and a member of a scope
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        var application = await SeedApplicationAsync(scope, person);
        await SeedTokensAsync(person);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeletePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}/hard");

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(person.PublicId, response.Body?.Data?.Id);
        Assert.Equal(1, response.Body?.Data?.DeletedApplicationCount);
        Assert.Equal(2, response.Body?.Data?.DeletedTokenCount);

        // Then — database state: the person, their application, tokens, and membership row are gone,
        // and the scope itself survives
        await using var context = db.CreateContext();
        Assert.False(await context.Persons.AsNoTracking().AnyAsync(p => p.Id == person.Id));
        Assert.False(await context.Applications.AsNoTracking().AnyAsync(a => a.Id == application.Id));
        Assert.False(await context.PasswordResetTokens.AsNoTracking().AnyAsync(t => t.PersonId == person.Id));
        Assert.False(await context.EmailVerificationTokens.AsNoTracking().AnyAsync(t => t.PersonId == person.Id));
        Assert.False(await context.ScopeUsers.AsNoTracking().AnyAsync(su => su.PersonId == person.Id));
        Assert.True(await context.Scopes.AsNoTracking().AnyAsync(s => s.Id == scope.Id));
    }

    [FunctionalFact]
    public async Task GivenCoOwnedScope_WhenHardDeletePerson_ThenOwnerAndOwnershipRowAreRemoved()
    {
        // Given a scope with a second owner, so NFR-12 still holds after the deletion
        var scope = await SeedScopeAsync();
        var target = await SeedScopeAdminAsync(ownedScope: scope);
        var coOwner = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeletePersonCommandOutput?>>(
            $"/api/persons/{target.PublicId}/hard");

        // Then — the target and their ownership row are gone; the co-owner and the scope remain
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = db.CreateContext();
        Assert.False(await context.Persons.AsNoTracking().AnyAsync(p => p.Id == target.Id));
        Assert.False(await context.ScopeOwners.AsNoTracking().AnyAsync(so => so.PersonId == target.Id));
        Assert.True(await context.Persons.AsNoTracking().AnyAsync(p => p.Id == coOwner.Id));
        Assert.True(await context.ScopeOwners.AsNoTracking().AnyAsync(so => so.PersonId == coOwner.Id));
        Assert.True(await context.Scopes.AsNoTracking().AnyAsync(s => s.Id == scope.Id));
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedPerson_WhenHardDeletePerson_ThenPersonIsRemoved()
    {
        // Given an already soft-deleted person (Decision 1)
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeletePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}/hard");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await PersonExistsAsync(person));
    }

    [FunctionalFact]
    public async Task GivenUnknownPersonId_WhenHardDeletePerson_ThenNotFound()
    {
        // Given — AF-10a
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeletePersonCommandOutput?>>(
            $"/api/persons/{Guid.NewGuid()}/hard");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenSoleOwnerScopeAdmin_WhenHardDeletePerson_ThenConflict()
    {
        // Given a scope whose only owner is the target (AF-10b, NFR-12)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeletePersonCommandOutput?>>(
            $"/api/persons/{owner.PublicId}/hard");

        // Then — refused, and both the person and their ownership row survive
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var context = db.CreateContext();
        Assert.True(await context.Persons.AsNoTracking().AnyAsync(p => p.Id == owner.Id));
        Assert.True(await context.ScopeOwners.AsNoTracking().AnyAsync(so => so.PersonId == owner.Id));
    }

    [FunctionalFact]
    public async Task GivenActorTargetingThemselves_WhenHardDeletePerson_ThenForbidden()
    {
        // Given — AF-10c. The message is asserted because the role gate returns the same status.
        var scope = await SeedScopeAsync();
        var actor = await SeedUserAsync(scope);
        Authorize(TestTokens.For((int)actor.Id, (int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeletePersonCommandOutput?>>(
            $"/api/persons/{actor.PublicId}/hard");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(PersonMessages.CannotDeleteSelf, response.Body?.Errors ?? []);
        Assert.True(await PersonExistsAsync(actor));
    }

    [FunctionalFact]
    public async Task GivenScopeAdminRole_WhenHardDeletePerson_ThenForbidden()
    {
        // Given a Scope Admin, whom the [RoleRequirement] gate keeps out entirely — unlike UC-09's
        // logical delete, hard deletion is System-Admin-only
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.For((int)owner.Id, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeletePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}/hard");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await PersonExistsAsync(person));
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenHardDeletePerson_ThenForbidden()
    {
        // Given a plain User
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeletePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}/hard");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await PersonExistsAsync(person));
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenHardDeletePerson_ThenUnauthorized()
    {
        // Given a person but no bearer token on the gateway
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeletePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}/hard");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(await PersonExistsAsync(person));
    }
}
```

- [ ] **Step 2: Run the functional suite**

Run: `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional"`
Expected: PASS — every existing functional test plus the nine new ones. Requires Docker running for Testcontainers.

- [ ] **Step 3: Run the whole suite**

Run: `dotnet test src/ArturRios.Heimdall.sln`
Expected: PASS, 0 failed.

- [ ] **Step 4: Commit**

```bash
git add tests/Presentation/ArturRios.Heimdall.WebApi.Tests/PersonControllerHardDeleteTests.cs
git commit -m "test: cover hard delete person endpoint (UC-10)"
```

---

## Task 5: Align the specification

**Files:**
- Modify: `docs/requirements/Use Case Specification Document.md`

**Interfaces:**
- Consumes: the behaviour built in Tasks 1–4.
- Produces: a UC-10 section that matches the code, flow for flow.

- [ ] **Step 1: Update the UC-10 table and flows**

In the `### UC-10: Hard Delete Person` section:

- Preconditions: add that the actor may not be the person being hard-deleted.
- Main flow step 1: make the endpoint explicit — `System Admin sends a hard delete request to DELETE /api/persons/{id}/hard`.
- Main flow: add a step recording that the system loads the person **in any deletion state**, so a logically deleted person can still be hard-deleted.
- Alternative flows: add the row

  | ID | Condition | Outcome |
  | ---- | ----------- | --------- |
  | AF-10c | Actor is the person being hard-deleted | Return `403 Forbidden` |

- Add a note after the table, in the style of UC-09's notes, covering two things: why AF-10c exists
  (a System Admin may delete "any person", which literally includes themselves; refused so one
  irreversible call cannot destroy the caller's own account), and why AF-10b applies even when the
  target is **already logically deleted** — the opposite of UC-09, whose AF-09b idempotent success wins
  over its last-owner guard. NFR-12 names hard-deleting the last owning person explicitly, and the
  guard keeps every scope row backed by at least one `SCOPE_OWNER` row; the escape hatch is adding
  another owner (UC-21) or hard-deleting the scope (UC-05) first.

- [ ] **Step 2: Verify**

Read the UC-10 section back and confirm each documented flow maps to a branch in
`HardDeletePersonCommandHandler` and to a test in Tasks 2 and 4. There must be no documented flow
without a test, and no handler branch without a documented flow.

- [ ] **Step 3: Commit**

```bash
git add "docs/requirements/Use Case Specification Document.md"
git commit -m "docs: align UC-10 spec with implemented behavior"
```

---

## After the merge (Gate 4)

- [ ] Mark UC-10 ✅ in the README status tracker.
- [ ] Confirm issue [#11](https://github.com/artur-rios/heimdall-api/issues/11) is in **Done** and closed.
