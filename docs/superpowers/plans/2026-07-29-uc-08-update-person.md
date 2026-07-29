# UC-08 Update Person Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement UC-08 (Update Person) — one `PUT /api/persons/{id}` endpoint that changes a person's name and email for the actors the use case permits, and their role for a System Admin, keeping the scope associations and email-uniqueness rules intact.

**Architecture:** CQRS write flow mirroring UC-03 (Update Scope) and UC-06. One command, validator, handler, and output; the endpoint joins the existing `PersonController`. The handler returns `DataOutput<T>` and never throws. Actor identity arrives through the `IActorScoped` plumbing UC-07 moved into `Shared`.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core (PostgreSQL), FluentValidation, ArturRios.Mediator / .Output / .Data.Relational.Core / .Util.WebApi; xUnit + Moq + Bogus + Testcontainers for tests.

## Global Constraints

- **Design of record:** `docs/superpowers/specs/2026-07-29-uc-08-update-person-design.md`. Every decision below traces to it.
- **No schema change / no EF migration** — `person`, `scope_user`, `scope_owner` already exist from `InitialCreate`.
- **Identifiers:** routes, inputs and outputs use `PublicId` (GUID); joins and FKs use internal `Id` (bigint). Never expose or accept an internal `Id` (NFR-15). Never return `PasswordHash` / `Salt`.
- **Handlers return `DataOutput<T>` and never throw.** Failures are errors carrying a canonical `PersonMessages` value, which `ResponseResolver` maps through `PersonMessageMap.StatusCodes`.
- **Roles:** `SystemAdmin = 1`, `ScopeAdmin = 2`, `User = 3`; the seeder guarantees `Role.Id == (long)Roles`.
- **Acting user:** the auth middleware attaches `AuthenticatedUser(int Id, int Role)` to `HttpContext.Items["User"]`; the `Id` claim is the person's **internal** `Id`. `PersonController.ApplyActor` copies it onto any `IActorScoped`.
- **Role transitions:** only `→ SystemAdmin` is supported, and only for a System Admin actor. Everything else is rejected — never guess a scope.
- **Join-row removal:** there is no repository for `ScopeUser`/`ScopeOwner` (the generic repository requires `Entity`). Rows are removed by severing the tracked navigation on `Person` and updating it; EF deletes the orphans because both relationships are required with `DeleteBehavior.Cascade`. The handler must therefore `Include` both navigations. `AsyncFakeRepository<Person>` does **not** model this cascade — unit tests assert the navigation is cleared, functional tests assert the row is gone from the database.
- **Tests:** unit tests use `AsyncFakeRepository<T>` and Moq for the validator and `IScopeOwnershipChecker`; functional tests derive from `WebApiTest<Program>`, join `[Collection(nameof(FunctionalCollection))]`, authorize via `TestTokens`, and assert response **and** database state via `db.CreateContext()`. GWT naming, `// Given` / `// When` / `// Then`, `[UnitFact]` / `[FunctionalFact]`.
- **Run filters:** `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"` and `--filter "Category=Functional"`.
- **Commit style:** lowercase Conventional Commits subject, ≤50 chars, imperative; body wrapped at 72; trailer `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.

---

## File Structure

**New — production:**
- `src/Application/ArturRios.IdentityManager.Command/Input/UpdatePersonCommand.cs`
- `src/Application/ArturRios.IdentityManager.Command/Input/Validation/UpdatePersonCommandValidator.cs`
- `src/Application/ArturRios.IdentityManager.Command/Output/UpdatePersonCommandOutput.cs`
- `src/Application/ArturRios.IdentityManager.Command/Handlers/UpdatePersonCommandHandler.cs`

**Modified — production:**
- `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessages.cs` — six new messages.
- `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessageMap.cs` — their status codes.
- `src/Presentation/ArturRios.IdentityManager.WebApi/Controllers/PersonController.cs` — one PUT action.
- `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs` — validator + handler registration.

**New — tests:**
- `tests/Application/ArturRios.IdentityManager.Command.Tests/UpdatePersonCommandValidatorTests.cs`
- `tests/Application/ArturRios.IdentityManager.Command.Tests/UpdatePersonCommandHandlerTests.cs`
- `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerUpdateTests.cs`

**Modified — docs:**
- `docs/requirements/Use Case Specification Document.md` — UC-08 brought in line with the behaviour.

---

## Task 1: Command, validator, output, and messages

Everything the handler needs, with the validator unit-tested. No handler yet.

**Files:**
- Create: `src/Application/ArturRios.IdentityManager.Command/Input/UpdatePersonCommand.cs`
- Create: `src/Application/ArturRios.IdentityManager.Command/Input/Validation/UpdatePersonCommandValidator.cs`
- Create: `src/Application/ArturRios.IdentityManager.Command/Output/UpdatePersonCommandOutput.cs`
- Modify: `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessages.cs`
- Modify: `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessageMap.cs`
- Test: `tests/Application/ArturRios.IdentityManager.Command.Tests/UpdatePersonCommandValidatorTests.cs`

**Interfaces:**
- Consumes: `IActorScoped` from `ArturRios.IdentityManager.Shared.Security` (UC-07).
- Produces:
  - `UpdatePersonCommand : BaseCommand, IActorScoped` with `Guid Id`, `string Name`, `string Email`, `int? RoleId`, `long ActingPersonId`, `int ActingRole`.
  - `UpdatePersonCommandValidator : AbstractValidator<UpdatePersonCommand>`.
  - `UpdatePersonCommandOutput : CommandOutput` with `Guid Id`, `string Name`, `string Email`, `int Role`, `bool EmailVerified`, `Guid? ScopeId`, `IEnumerable<Guid> OwnedScopeIds`, `DateTime CreatedAt`, `DateTime UpdatedAt`.
  - `PersonMessages.PersonUpdatedSuccessfully`, `.NotAuthorizedToUpdatePerson`, `.RoleChangeRequiresSystemAdmin`, `.UnsupportedRoleTransition`, `.ScopeWouldLoseLastOwner`, `.UnknownRole`.

- [ ] **Step 1: Add the six messages**

Append inside the class in `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessages.cs`:

```csharp
    /// <summary>UC-08 success: the person was updated.</summary>
    public const string PersonUpdatedSuccessfully = "Person updated successfully.";

    /// <summary>UC-08: the caller is not allowed to update the requested person.</summary>
    public const string NotAuthorizedToUpdatePerson = "You are not allowed to update this person.";

    /// <summary>AF-08c: only a System Admin may change a person's role.</summary>
    public const string RoleChangeRequiresSystemAdmin = "Only a System Admin may change a person's role.";

    /// <summary>
    ///     UC-08: the requested role change would need a target scope the request does not carry.
    ///     Only a change to SystemAdmin is supported here.
    /// </summary>
    public const string UnsupportedRoleTransition =
        "Only a change to SystemAdmin is supported here. To make a person a scope owner, use the " +
        "scope owner endpoints.";

    /// <summary>NFR-12: the change would leave a scope without any owner.</summary>
    public const string ScopeWouldLoseLastOwner =
        "This change would leave a scope without an owner. Add another owner first.";

    /// <summary>UC-08: the supplied role is not one of the three defined roles.</summary>
    public const string UnknownRole = "Role must be SystemAdmin, ScopeAdmin, or User.";
```

- [ ] **Step 2: Map them to status codes**

In `src/Application/ArturRios.IdentityManager.Shared/Messages/PersonMessageMap.cs`, add a comma to
the last existing entry and append:

```csharp
        // UC-08 main flow — person updated.
        [PersonMessages.PersonUpdatedSuccessfully] = HttpStatusCodes.Ok,
        // UC-08 — caller may not update the person; AF-08c for the role-change case.
        [PersonMessages.NotAuthorizedToUpdatePerson] = HttpStatusCodes.Forbidden,
        [PersonMessages.RoleChangeRequiresSystemAdmin] = HttpStatusCodes.Forbidden,
        // UC-08 — the transition needs a scope the request does not carry, or the role is unknown.
        [PersonMessages.UnsupportedRoleTransition] = HttpStatusCodes.BadRequest,
        [PersonMessages.UnknownRole] = HttpStatusCodes.BadRequest,
        // NFR-12 — the change would strip a scope of its last owner.
        [PersonMessages.ScopeWouldLoseLastOwner] = HttpStatusCodes.Conflict
```

Update the class summary to read `following the UC-06, UC-07 and UC-08 flows`.

- [ ] **Step 3: Create the command**

Create `src/Application/ArturRios.IdentityManager.Command/Input/UpdatePersonCommand.cs`:

```csharp
using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to update a person's name, email, and — for a System Admin — role (UC-08). The person
///     is addressed by <see cref="Id" />, bound from the route. PUT semantics: <see cref="Name" />
///     and <see cref="Email" /> are replaced. <see cref="RoleId" /> is optional; <c>null</c> leaves
///     the role unchanged, which is what every non-System-Admin caller sends.
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller and are never bound from the body.
/// </summary>
public class UpdatePersonCommand : BaseCommand, IActorScoped
{
    /// <summary>Public identifier of the person to update (bound from the route).</summary>
    public Guid Id { get; set; }

    /// <summary>New full name. Required, max 200 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>New email address. Required; changing it clears <c>EmailVerified</c> (UC-08 step 4).</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>New role value (see <c>Roles</c>), or <c>null</c> to leave the role unchanged.</summary>
    public int? RoleId { get; set; }

    public long ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
```

- [ ] **Step 4: Create the output**

Create `src/Application/ArturRios.IdentityManager.Command/Output/UpdatePersonCommandOutput.cs`:

```csharp
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Output;

/// <summary>
///     The person as it stands after a UC-08 update. Exposes only external-facing identifiers and
///     has no field for <c>PasswordHash</c> or <c>Salt</c>.
/// </summary>
public class UpdatePersonCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the updated person.</summary>
    public Guid Id { get; set; }

    /// <summary>Full name after the update.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Email address after the update.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Role value after the update (see <c>Roles</c>).</summary>
    public int Role { get; set; }

    /// <summary>Whether the email is verified; always <c>false</c> straight after an email change.</summary>
    public bool EmailVerified { get; set; }

    /// <summary>Public identifier of the scope the person belongs to as a User; <c>null</c> otherwise.</summary>
    public Guid? ScopeId { get; set; }

    /// <summary>Public identifiers of the scopes the person owns; empty for non-owners.</summary>
    public IEnumerable<Guid> OwnedScopeIds { get; set; } = new List<Guid>();

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Update timestamp, stamped by this operation.</summary>
    public DateTime UpdatedAt { get; set; }
}
```

- [ ] **Step 5: Write the failing validator tests**

Create `tests/Application/ArturRios.IdentityManager.Command.Tests/UpdatePersonCommandValidatorTests.cs`:

```csharp
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Input.Validation;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for UpdatePersonCommandValidator (UC-08 shape validation). Business rules that need
// data access — existence, authorization, email uniqueness, ownership — are the handler's job.
public class UpdatePersonCommandValidatorTests
{
    private static UpdatePersonCommand Valid() => new()
    {
        Id = Guid.NewGuid(), Name = "Ana", Email = "ana@test.local"
    };

    [UnitFact]
    public async Task GivenValidCommandWithoutRole_WhenValidating_ThenIsValid()
    {
        // Given
        var validator = new UpdatePersonCommandValidator();

        // When
        var result = await validator.ValidateAsync(Valid());

        // Then
        Assert.True(result.IsValid);
    }

    [UnitTheory]
    [InlineData((int)Roles.SystemAdmin)]
    [InlineData((int)Roles.ScopeAdmin)]
    [InlineData((int)Roles.User)]
    public async Task GivenDefinedRole_WhenValidating_ThenIsValid(int role)
    {
        // Given
        var validator = new UpdatePersonCommandValidator();
        var command = Valid();
        command.RoleId = role;

        // When
        var result = await validator.ValidateAsync(command);

        // Then
        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenUndefinedRole_WhenValidating_ThenReturnsUnknownRole()
    {
        // Given
        var validator = new UpdatePersonCommandValidator();
        var command = Valid();
        command.RoleId = 99;

        // When
        var result = await validator.ValidateAsync(command);

        // Then
        Assert.False(result.IsValid);
        Assert.Contains(PersonMessages.UnknownRole, result.Errors.Select(x => x.ErrorMessage));
    }

    [UnitFact]
    public async Task GivenEmptyName_WhenValidating_ThenReturnsNameRequired()
    {
        // Given
        var validator = new UpdatePersonCommandValidator();
        var command = Valid();
        command.Name = string.Empty;

        // When
        var result = await validator.ValidateAsync(command);

        // Then
        Assert.False(result.IsValid);
        Assert.Contains(PersonMessages.NameRequired, result.Errors.Select(x => x.ErrorMessage));
    }

    [UnitFact]
    public async Task GivenNameOver200Characters_WhenValidating_ThenReturnsNameTooLong()
    {
        // Given
        var validator = new UpdatePersonCommandValidator();
        var command = Valid();
        command.Name = new string('a', 201);

        // When
        var result = await validator.ValidateAsync(command);

        // Then
        Assert.False(result.IsValid);
        Assert.Contains(PersonMessages.NameTooLong, result.Errors.Select(x => x.ErrorMessage));
    }

    [UnitFact]
    public async Task GivenEmptyEmail_WhenValidating_ThenReturnsEmailRequired()
    {
        // Given
        var validator = new UpdatePersonCommandValidator();
        var command = Valid();
        command.Email = string.Empty;

        // When
        var result = await validator.ValidateAsync(command);

        // Then
        Assert.False(result.IsValid);
        Assert.Contains(PersonMessages.EmailRequired, result.Errors.Select(x => x.ErrorMessage));
    }

    [UnitFact]
    public async Task GivenMalformedEmail_WhenValidating_ThenReturnsEmailInvalid()
    {
        // Given
        var validator = new UpdatePersonCommandValidator();
        var command = Valid();
        command.Email = "not-an-email";

        // When
        var result = await validator.ValidateAsync(command);

        // Then
        Assert.False(result.IsValid);
        Assert.Contains(PersonMessages.EmailInvalid, result.Errors.Select(x => x.ErrorMessage));
    }
}
```

- [ ] **Step 6: Run the tests to verify they fail**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"`
Expected: compilation error — `UpdatePersonCommandValidator` does not exist.

- [ ] **Step 7: Create the validator**

Create `src/Application/ArturRios.IdentityManager.Command/Input/Validation/UpdatePersonCommandValidator.cs`:

```csharp
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using FluentValidation;

namespace ArturRios.IdentityManager.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="UpdatePersonCommand" /> (UC-08 step 2). Business rules that
///     need data access — existence, authorization, email uniqueness, scope ownership — are enforced
///     by the handler.
/// </summary>
public class UpdatePersonCommandValidator : AbstractValidator<UpdatePersonCommand>
{
    public UpdatePersonCommandValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty().WithMessage(PersonMessages.NameRequired)
            .MaximumLength(200).WithMessage(PersonMessages.NameTooLong);

        RuleFor(command => command.Email)
            .NotEmpty().WithMessage(PersonMessages.EmailRequired)
            .EmailAddress().WithMessage(PersonMessages.EmailInvalid);

        // The role is optional: null means "leave it unchanged". When supplied it must name one of
        // the three defined roles; whether the transition is *allowed* is the handler's decision.
        RuleFor(command => command.RoleId)
            .Must(role => role is (int)Roles.SystemAdmin or (int)Roles.ScopeAdmin or (int)Roles.User)
            .When(command => command.RoleId is not null)
            .WithMessage(PersonMessages.UnknownRole);
    }
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"`
Expected: PASS — all nine `UpdatePersonCommandValidatorTests` green, nothing else broken.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: add update person command and validator (UC-08)"
```

---

## Task 2: The update handler

**Files:**
- Create: `src/Application/ArturRios.IdentityManager.Command/Handlers/UpdatePersonCommandHandler.cs`
- Test: `tests/Application/ArturRios.IdentityManager.Command.Tests/UpdatePersonCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `UpdatePersonCommand`, `UpdatePersonCommandOutput`, the new `PersonMessages` (Task 1); `IScopeOwnershipChecker` from `ArturRios.IdentityManager.Shared.Services` (UC-07).
- Produces: `UpdatePersonCommandHandler(IValidator<UpdatePersonCommand> validator, IAsyncReadOnlyRepository<Person> personReader, IAsyncRepository<Person> personWriter, IScopeOwnershipChecker scopeOwnership)` implementing `ICommandHandlerAsync<UpdatePersonCommand, UpdatePersonCommandOutput>`.

- [ ] **Step 1: Write the failing handler tests**

Create `tests/Application/ArturRios.IdentityManager.Command.Tests/UpdatePersonCommandHandlerTests.cs`:

```csharp
using ArturRios.IdentityManager.Command.Handlers;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for UpdatePersonCommandHandler (UC-08). Cover the main flow for each permitted actor,
// AF-08a (not found), AF-08b (email conflict), AF-08c (role change by a non-System-Admin), the
// unsupported transitions, and the NFR-12 last-owner guard.
//
// Note: AsyncFakeRepository is an in-memory list and models no EF cascade, so these tests assert
// that the scope navigation was cleared. That the scope_user / scope_owner row actually disappears
// is asserted by PersonControllerUpdateTests against PostgreSQL.
public class UpdatePersonCommandHandlerTests
{
    private static Scope Scope(long id) => new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}" };

    private static Person User(long id, Scope scope, string email = "user@test.local") => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"user-{id}",
        Email = email,
        RoleId = (long)Roles.User,
        EmailVerified = true,
        ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope }
    };

    private static Person ScopeAdmin(long id, string email = "admin@test.local", params Scope[] owned) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"admin-{id}",
        Email = email,
        RoleId = (long)Roles.ScopeAdmin,
        EmailVerified = true,
        ScopeOwnerships = owned.Select(scope => new ScopeOwner { ScopeId = scope.Id, Scope = scope }).ToList()
    };

    private static IValidator<UpdatePersonCommand> PassingValidator()
    {
        var validator = new Mock<IValidator<UpdatePersonCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<UpdatePersonCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        return validator.Object;
    }

    private static IScopeOwnershipChecker Ownership(bool allowed)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);

        return checker.Object;
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

    private static UpdatePersonCommandHandler HandlerFor(
        AsyncFakeRepository<Person> persons, bool ownershipAllowed = true) =>
        new(PassingValidator(), persons, persons, Ownership(ownershipAllowed));

    private static UpdatePersonCommand CommandFor(Person target, int actingRole, long actingPersonId) => new()
    {
        Id = target.PublicId,
        Name = target.Name,
        Email = target.Email,
        ActingRole = actingRole,
        ActingPersonId = actingPersonId
    };

    [UnitFact]
    public async Task GivenSystemAdminActor_WhenUpdatingNameAndEmail_ThenPersonIsUpdated()
    {
        // Given
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: 99);
        command.Name = "Renamed";
        command.Email = "renamed@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal("Renamed", output.Data!.Name);
        Assert.Equal("renamed@test.local", output.Data.Email);
        Assert.False(output.Data.EmailVerified);
        Assert.Contains(PersonMessages.PersonUpdatedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenUserUpdatingSelf_WhenUpdatingName_ThenPersonIsUpdated()
    {
        // Given
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.Id);
        command.Name = "Renamed";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal("Renamed", output.Data!.Name);
    }

    [UnitFact]
    public async Task GivenUnchangedEmail_WhenUpdating_ThenEmailVerifiedIsPreserved()
    {
        // Given a verified person whose email is resubmitted unchanged
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: 99);
        command.Name = "Renamed";

        // When
        var output = await handler.HandleAsync(command);

        // Then — no false conflict, and the verification flag survives
        Assert.True(output.Success);
        Assert.True(output.Data!.EmailVerified);
    }

    [UnitFact]
    public async Task GivenOwningScopeAdmin_WhenUpdatingScopeUser_ThenPersonIsUpdated()
    {
        // Given a ScopeAdmin who owns the target User's scope
        var scope = Scope(1);
        var target = User(10, scope);
        var actor = ScopeAdmin(11, "owner@test.local", scope);
        var persons = await PersonsWith(target, actor);
        var handler = HandlerFor(persons, ownershipAllowed: true);
        var command = CommandFor(target, (int)Roles.ScopeAdmin, actor.Id);
        command.Name = "Renamed";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal("Renamed", output.Data!.Name);
    }

    [UnitFact]
    public async Task GivenNonOwningScopeAdmin_WhenUpdatingScopeUser_ThenReturnsNotAuthorized()
    {
        // Given a ScopeAdmin the ownership checker rejects
        var scope = Scope(1);
        var target = User(10, scope);
        var actor = ScopeAdmin(11, "outsider@test.local");
        var persons = await PersonsWith(target, actor);
        var handler = HandlerFor(persons, ownershipAllowed: false);

        // When
        var output = await handler.HandleAsync(CommandFor(target, (int)Roles.ScopeAdmin, actor.Id));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotAuthorizedToUpdatePerson, output.Errors);
    }

    [UnitFact]
    public async Task GivenUserUpdatingAnotherPerson_WhenUpdating_ThenReturnsNotAuthorized()
    {
        // Given two Users of the same scope
        var scope = Scope(1);
        var target = User(10, scope);
        var actor = User(11, scope, "other@test.local");
        var persons = await PersonsWith(target, actor);
        var handler = HandlerFor(persons);

        // When
        var output = await handler.HandleAsync(CommandFor(target, (int)Roles.User, actor.Id));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotAuthorizedToUpdatePerson, output.Errors);
    }

    [UnitFact]
    public async Task GivenUnknownPersonId_WhenUpdating_ThenReturnsPersonNotFound()
    {
        // Given an empty store (AF-08a)
        var persons = await PersonsWith();
        var handler = HandlerFor(persons);

        // When
        var output = await handler.HandleAsync(new UpdatePersonCommand
        {
            Id = Guid.NewGuid(), Name = "Ana", Email = "ana@test.local",
            ActingRole = (int)Roles.SystemAdmin, ActingPersonId = 1
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedPerson_WhenUpdating_ThenReturnsPersonNotFound()
    {
        // Given a logically deleted person (AF-08a)
        var scope = Scope(1);
        var target = User(10, scope);
        target.IsDeleted = true;
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);

        // When
        var output = await handler.HandleAsync(CommandFor(target, (int)Roles.SystemAdmin, 99));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenEmailTakenByAnotherUserInScope_WhenUpdating_ThenReturnsEmailAlreadyExists()
    {
        // Given two Users in one scope (AF-08b, FR-PE-09 within scope)
        var scope = Scope(1);
        var target = User(10, scope);
        var other = User(11, scope, "taken@test.local");
        var persons = await PersonsWith(target, other);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, 99);
        command.Email = "taken@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
    }

    [UnitFact]
    public async Task GivenEmailTakenByAnotherAdmin_WhenUpdatingAdmin_ThenReturnsEmailAlreadyExists()
    {
        // Given two admins (AF-08b, FR-PE-09 system-wide)
        var scope = Scope(1);
        var target = ScopeAdmin(10, "admin@test.local", scope);
        var other = ScopeAdmin(11, "taken@test.local", scope);
        var persons = await PersonsWith(target, other);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, 99);
        command.Email = "taken@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
    }

    [UnitFact]
    public async Task GivenNonSystemAdminActor_WhenChangingRole_ThenReturnsRoleChangeRequiresSystemAdmin()
    {
        // Given an owning ScopeAdmin attempting a role change (AF-08c)
        var scope = Scope(1);
        var target = User(10, scope);
        var actor = ScopeAdmin(11, "owner@test.local", scope);
        var persons = await PersonsWith(target, actor);
        var handler = HandlerFor(persons, ownershipAllowed: true);
        var command = CommandFor(target, (int)Roles.ScopeAdmin, actor.Id);
        command.RoleId = (int)Roles.SystemAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.RoleChangeRequiresSystemAdmin, output.Errors);
    }

    [UnitFact]
    public async Task GivenSystemAdminPromotingUserToScopeAdmin_WhenUpdating_ThenReturnsUnsupportedTransition()
    {
        // Given a User being pushed to ScopeAdmin, which would need a target scope
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, 99);
        command.RoleId = (int)Roles.ScopeAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.UnsupportedRoleTransition, output.Errors);
    }

    [UnitFact]
    public async Task GivenSystemAdminPromotingUserToSystemAdmin_WhenUpdating_ThenScopeMembershipIsCleared()
    {
        // Given a User promoted to SystemAdmin, who must end up with no scope (FR-PE-10)
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, 99);
        command.RoleId = (int)Roles.SystemAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal((int)Roles.SystemAdmin, output.Data!.Role);
        Assert.Null(output.Data.ScopeId);
        Assert.Null(target.ScopeMembership);
    }

    [UnitFact]
    public async Task GivenScopeWithAnotherOwner_WhenPromotingOwnerToSystemAdmin_ThenOwnershipsAreCleared()
    {
        // Given a scope with two owners, so losing one leaves it owned (NFR-12 satisfied)
        var scope = Scope(1);
        var target = ScopeAdmin(10, "first@test.local", scope);
        var coOwner = ScopeAdmin(11, "second@test.local", scope);
        var persons = await PersonsWith(target, coOwner);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, 99);
        command.RoleId = (int)Roles.SystemAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal((int)Roles.SystemAdmin, output.Data!.Role);
        Assert.Empty(output.Data.OwnedScopeIds);
        Assert.Empty(target.ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenSoleOwner_WhenPromotingToSystemAdmin_ThenReturnsScopeWouldLoseLastOwner()
    {
        // Given a scope whose only owner is the target (NFR-12)
        var scope = Scope(1);
        var target = ScopeAdmin(10, "only@test.local", scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, 99);
        command.RoleId = (int)Roles.SystemAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeWouldLoseLastOwner, output.Errors);
    }

    [UnitFact]
    public async Task GivenNullRoleId_WhenUpdating_ThenRoleIsUnchanged()
    {
        // Given a User whose command carries no role
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, 99);
        command.Name = "Renamed";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal((int)Roles.User, output.Data!.Role);
        Assert.NotNull(target.ScopeMembership);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"`
Expected: compilation error — `UpdatePersonCommandHandler` does not exist.

- [ ] **Step 3: Implement the handler**

Create `src/Application/ArturRios.IdentityManager.Command/Handlers/UpdatePersonCommandHandler.cs`:

```csharp
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Command.Handlers;

/// <summary>
///     Handles <see cref="UpdatePersonCommand" /> (UC-08): validates the request, loads the person
///     (AF-08a), enforces the per-actor rule, applies an optional role change (AF-08c, NFR-12), then
///     the name and email — re-checking uniqueness per FR-PE-09 and clearing <c>EmailVerified</c>
///     when the address changes (AF-08b). All failures are returned as errors on the output rather
///     than thrown.
/// </summary>
public class UpdatePersonCommandHandler(
    IValidator<UpdatePersonCommand> validator,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Person> personWriter,
    IScopeOwnershipChecker scopeOwnership)
    : ICommandHandlerAsync<UpdatePersonCommand, UpdatePersonCommandOutput>
{
    public async Task<DataOutput<UpdatePersonCommandOutput?>> HandleAsync(UpdatePersonCommand command)
    {
        var output = DataOutput<UpdatePersonCommandOutput?>.New;

        // Step 2: validate input shape.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-08a: the person must exist and not be logically deleted. Both scope navigations are
        // included because severing them is how the join rows get deleted (see the design doc), and
        // each join row's Scope is included too because the response reports scope PublicIds.
        var person = await personReader.Query()
            .Include(x => x.ScopeMembership)
            .ThenInclude(membership => membership!.Scope)
            .Include(x => x.ScopeOwnerships)
            .ThenInclude(ownership => ownership.Scope)
            .FirstOrDefaultAsync(x => x.PublicId == command.Id && !x.IsDeleted);

        if (person is null)
        {
            return output.WithError(PersonMessages.PersonNotFound);
        }

        // UC-08 step 3: a System Admin may update anyone; anyone may update themselves; a Scope Admin
        // may update a User of a scope they own.
        if (!await MayUpdateAsync(command, person))
        {
            return output.WithError(PersonMessages.NotAuthorizedToUpdatePerson);
        }

        // UC-08 step 5: apply the role change, if one was asked for.
        var roleChange = await ApplyRoleChangeAsync(command, person);

        if (roleChange is not null)
        {
            return output.WithError(roleChange);
        }

        // UC-08 step 4: an email change re-checks uniqueness and clears the verification flag.
        var emailChanged = !string.Equals(person.Email, command.Email, StringComparison.OrdinalIgnoreCase);

        if (emailChanged)
        {
            if (await EmailTakenAsync(command, person))
            {
                return output.WithError(PersonMessages.EmailAlreadyExists);
            }

            person.EmailVerified = false;
        }

        // UC-08 step 6: apply and stamp UpdatedAt (no DB trigger maintains it).
        person.Name = command.Name;
        person.Email = command.Email;
        person.UpdatedAt = DateTime.UtcNow;

        var update = await personWriter.UpdateAsync(person);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        // UC-08 step 7: return the updated person.
        return output
            .WithData(new UpdatePersonCommandOutput
            {
                Id = person.PublicId,
                Name = person.Name,
                Email = person.Email,
                Role = (int)person.RoleId,
                EmailVerified = person.EmailVerified,
                ScopeId = person.ScopeMembership?.Scope.PublicId,
                OwnedScopeIds = person.ScopeOwnerships.Select(ownership => ownership.Scope.PublicId).ToList(),
                CreatedAt = person.CreatedAt,
                UpdatedAt = person.UpdatedAt
            })
            .WithMessage(PersonMessages.PersonUpdatedSuccessfully);
    }

    /// <summary>
    ///     UC-08 step 3. A System Admin may update any person; any actor may update their own record;
    ///     a Scope Admin may update a <c>User</c> belonging to a scope they own. Everything else is
    ///     denied.
    /// </summary>
    private async Task<bool> MayUpdateAsync(UpdatePersonCommand command, Person person)
    {
        if (command.ActingRole == (int)Roles.SystemAdmin || command.ActingPersonId == person.Id)
        {
            return true;
        }

        if (command.ActingRole != (int)Roles.ScopeAdmin ||
            person.RoleId != (long)Roles.User ||
            person.ScopeMembership is null)
        {
            return false;
        }

        return await scopeOwnership.ActorMayManageScopeAsync(
            command.ActingRole, command.ActingPersonId, person.ScopeMembership.ScopeId);
    }

    /// <summary>
    ///     UC-08 step 5. Returns <c>null</c> when there is nothing to do or the change was applied,
    ///     or the canonical message describing why the change was refused.
    ///     Only a change to <c>SystemAdmin</c> is supported: every other target role would need a
    ///     scope the request does not carry (FR-PE-02, FR-PE-11). A person becoming a System Admin
    ///     must end up with no scope association at all (FR-PE-10), so their membership and ownership
    ///     rows are severed — which deletes them, since both relationships are required and cascade.
    /// </summary>
    private async Task<string?> ApplyRoleChangeAsync(UpdatePersonCommand command, Person person)
    {
        if (command.RoleId is null || command.RoleId == (int)person.RoleId)
        {
            return null;
        }

        // AF-08c: only a System Admin may change a role.
        if (command.ActingRole != (int)Roles.SystemAdmin)
        {
            return PersonMessages.RoleChangeRequiresSystemAdmin;
        }

        if (command.RoleId != (int)Roles.SystemAdmin)
        {
            return PersonMessages.UnsupportedRoleTransition;
        }

        // NFR-12: a scope must always retain at least one owner. Gather the scopes somebody *other*
        // than this person owns; refuse if any scope this person owns is not among them.
        if (person.RoleId == (long)Roles.ScopeAdmin && person.ScopeOwnerships.Count > 0)
        {
            var ownedScopeIds = person.ScopeOwnerships.Select(ownership => ownership.ScopeId).ToList();

            var coOwnedScopeIds = await personReader.Query()
                .Where(other => other.Id != person.Id)
                .SelectMany(other => other.ScopeOwnerships.Select(ownership => ownership.ScopeId))
                .Distinct()
                .ToListAsync();

            if (ownedScopeIds.Any(scopeId => !coOwnedScopeIds.Contains(scopeId)))
            {
                return PersonMessages.ScopeWouldLoseLastOwner;
            }
        }

        person.RoleId = (long)Roles.SystemAdmin;
        person.ScopeMembership = null;
        person.ScopeOwnerships.Clear();

        return null;
    }

    /// <summary>
    ///     FR-PE-09, evaluated against the role the person will have after this update: a
    ///     <c>User</c>'s email is unique within their scope, an admin's is unique system-wide. The
    ///     person being updated is excluded so resubmitting their own address is not a conflict.
    ///     Compared case-insensitively (<c>LOWER()</c> in SQL), as UC-06 does.
    /// </summary>
    private async Task<bool> EmailTakenAsync(UpdatePersonCommand command, Person person)
    {
        var email = command.Email.ToLower();

        if (person.RoleId == (long)Roles.User && person.ScopeMembership is not null)
        {
            var scopeId = person.ScopeMembership.ScopeId;

            return await personReader.Query().AnyAsync(other =>
                other.Id != person.Id && !other.IsDeleted && other.Email.ToLower() == email &&
                other.ScopeMembership != null && other.ScopeMembership.ScopeId == scopeId);
        }

        return await personReader.Query().AnyAsync(other =>
            other.Id != person.Id && !other.IsDeleted && other.Email.ToLower() == email &&
            (other.RoleId == (long)Roles.SystemAdmin || other.RoleId == (long)Roles.ScopeAdmin));
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"`
Expected: PASS — all sixteen `UpdatePersonCommandHandlerTests` green, nothing else broken.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add update person handler (UC-08)"
```

---

## Task 3: Endpoint, DI, and functional coverage

Like UC-07's Task 6, this task is **not test-first**: the route and its registration go in before the
functional tests, because a functional test cannot fail for the right reason against a route that
does not exist — it returns 404 for every case, including the ones expecting 404.

**Files:**
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/Controllers/PersonController.cs`
- Modify: `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs`
- Test: `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerUpdateTests.cs`

**Interfaces:**
- Consumes: `UpdatePersonCommand`, `UpdatePersonCommandOutput`, `UpdatePersonCommandHandler`, `UpdatePersonCommandValidator` (Tasks 1–2).
- Produces: `PUT /api/persons/{id}`.

- [ ] **Step 1: Add the controller action**

In `src/Presentation/ArturRios.IdentityManager.WebApi/Controllers/PersonController.cs`, add this
action after `CreateScopeOwner` and before the `GetById` read action:

```csharp
    /// <summary>
    ///     Updates a person's name and email, and — for a System Admin — their role (UC-08). Open to
    ///     any authenticated actor because a User may update their own record; the per-actor rule and
    ///     the role-change restriction (AF-08c) are enforced by the handler.
    /// </summary>
    [HttpPut("persons/{id:guid}")]
    public async Task<ActionResult<DataOutput<UpdatePersonCommandOutput?>>> Update(
        Guid id, [FromBody] UpdatePersonCommand command)
    {
        command.Id = id;
        ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<UpdatePersonCommand, UpdatePersonCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }
```

- [ ] **Step 2: Register the validator and handler**

In `src/Presentation/ArturRios.IdentityManager.WebApi/Startup.cs`, after the existing
`CreateScopeOwnerCommand` registrations, add:

```csharp
        Builder.Services.AddScoped<IValidator<UpdatePersonCommand>, UpdatePersonCommandValidator>();
        Builder.Services
            .AddScoped<ICommandHandlerAsync<UpdatePersonCommand, UpdatePersonCommandOutput>, UpdatePersonCommandHandler>();
```

- [ ] **Step 3: Write the functional tests**

Create `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/PersonControllerUpdateTests.cs`:

```csharp
using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.WebApi.Tests;

// Functional tests for PUT /api/persons/{id} (UC-08): the main flow for each permitted actor,
// AF-08a (404), AF-08b (409), AF-08c (403), the unsupported transition (400), the NFR-12 last-owner
// conflict (409), and the unauthenticated flow (401). Asserts response and database state, including
// that promoting a person to SystemAdmin really removes their join row.
[Collection(nameof(FunctionalCollection))]
public class PersonControllerUpdateTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
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

    private async Task<Person> SeedUserAsync(Scope scope, string? email = null)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Member",
            Email = email ?? UniqueEmail("user"),
            RoleId = (long)Roles.User,
            EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<Person> SeedScopeAdminAsync(Scope? ownedScope = null, string? email = null)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Admin",
            Email = email ?? UniqueEmail("admin"),
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

    private static UpdatePersonCommand Body(string name, string email, int? roleId = null) =>
        new() { Name = name, Email = email, RoleId = roleId };

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenPutPerson_ThenNameAndEmailAreUpdated()
    {
        // Given
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var email = UniqueEmail("renamed");

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}", Body("Renamed", email));

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", response.Body?.Data?.Name);
        Assert.False(response.Body?.Data?.EmailVerified);

        // Then — database state: the email change cleared the verification flag
        await using var context = db.CreateContext();
        var stored = await context.Persons.AsNoTracking().FirstAsync(p => p.PublicId == person.PublicId);
        Assert.Equal("Renamed", stored.Name);
        Assert.Equal(email, stored.Email);
        Assert.False(stored.EmailVerified);
    }

    [FunctionalFact]
    public async Task GivenUserUpdatingSelf_WhenPutPerson_ThenPersonIsUpdated()
    {
        // Given a User authenticated as themselves
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.For((int)person.Id, (int)Roles.User));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}", Body("Self Renamed", person.Email));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Self Renamed", response.Body?.Data?.Name);
    }

    [FunctionalFact]
    public async Task GivenOwnerScopeAdmin_WhenPutScopeUser_ThenPersonIsUpdated()
    {
        // Given a ScopeAdmin who owns the User's scope
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.For((int)owner.Id, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}", Body("Owner Renamed", person.Email));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Owner Renamed", response.Body?.Data?.Name);
    }

    [FunctionalFact]
    public async Task GivenNonOwningScopeAdmin_WhenPutScopeUser_ThenForbidden()
    {
        // Given a ScopeAdmin who does not own the User's scope
        var scope = await SeedScopeAsync();
        var outsider = await SeedScopeAdminAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.For((int)outsider.Id, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}", Body("Nope", person.Email));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUserUpdatingAnotherPerson_WhenPutPerson_ThenForbidden()
    {
        // Given two Users of the same scope
        var scope = await SeedScopeAsync();
        var actor = await SeedUserAsync(scope);
        var target = await SeedUserAsync(scope);
        Authorize(TestTokens.For((int)actor.Id, (int)Roles.User));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{target.PublicId}", Body("Nope", target.Email));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminChangingRole_WhenPutPerson_ThenForbidden()
    {
        // Given an owning ScopeAdmin attempting a role change (AF-08c)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.For((int)owner.Id, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}",
            Body("Member", person.Email, (int)Roles.SystemAdmin));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenSystemAdminPromotingUserToSystemAdmin_WhenPutPerson_ThenScopeUserRowIsRemoved()
    {
        // Given a User in a scope (FR-PE-10: a System Admin belongs to no scope)
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}",
            Body("Member", person.Email, (int)Roles.SystemAdmin));

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((int)Roles.SystemAdmin, response.Body?.Data?.Role);
        Assert.Null(response.Body?.Data?.ScopeId);

        // Then — database state: the join row is gone, the person remains
        await using var context = db.CreateContext();
        Assert.False(await context.ScopeUsers.AnyAsync(su => su.PersonId == person.Id));
        var stored = await context.Persons.AsNoTracking().FirstAsync(p => p.Id == person.Id);
        Assert.Equal((long)Roles.SystemAdmin, stored.RoleId);
    }

    [FunctionalFact]
    public async Task GivenSystemAdminPromotingUserToScopeAdmin_WhenPutPerson_ThenBadRequest()
    {
        // Given a transition that would need a target scope the request does not carry
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}",
            Body("Member", person.Email, (int)Roles.ScopeAdmin));

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenSoleOwner_WhenPromotingToSystemAdmin_ThenConflict()
    {
        // Given a scope whose only owner is the target (NFR-12)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{owner.PublicId}",
            Body("Admin", owner.Email, (int)Roles.SystemAdmin));

        // Then — refused, and the ownership row survives
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var context = db.CreateContext();
        Assert.True(await context.ScopeOwners.AnyAsync(so => so.PersonId == owner.Id));
    }

    [FunctionalFact]
    public async Task GivenEmailTakenInScope_WhenPutPerson_ThenConflict()
    {
        // Given two Users in one scope (AF-08b)
        var scope = await SeedScopeAsync();
        var first = await SeedUserAsync(scope);
        var second = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When the second takes the first's email
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{second.PublicId}", Body("Member", first.Email));

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnknownPersonId_WhenPutPerson_ThenNotFound()
    {
        // Given (AF-08a)
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{Guid.NewGuid()}", Body("Ghost", UniqueEmail("ghost")));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenInvalidPayload_WhenPutPerson_ThenBadRequest()
    {
        // Given an empty name
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}", Body(string.Empty, person.Email));

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPutPerson_ThenUnauthorized()
    {
        // Given a person but no bearer token on the gateway
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}", Body("Nope", person.Email));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

These are the repository's first functional tests to issue a PUT. The helper is
`ArturRios.Util.Http.HttpGateway.PutAsync<T>(string route, object body)`, alongside the `GetAsync`
and `PostAsync` the existing suites use.

- [ ] **Step 4: Run both suites**

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"`
Expected: PASS.

Run: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Functional"`
Expected: PASS — all thirteen `PersonControllerUpdateTests` green, and the existing functional
suites still green.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: expose update person endpoint (UC-08)"
```

---

## Task 4: Bring UC-08's specification in line

**Files:**
- Modify: `docs/requirements/Use Case Specification Document.md`

- [ ] **Step 1: Update the UC-08 section**

Rewrite the UC-08 main flow and alternative flows so the document describes what the API does:

- Step 3: keep the per-actor rule, and state explicitly that any actor may update their own record.
- Step 5: record that only a change to `SystemAdmin` is supported, that it removes the person's
  `SCOPE_USER` / `SCOPE_OWNER` rows per FR-PE-10, and that transitions needing a target scope belong
  to UC-21 / UC-23.
- Add a note under the flows recording the FR-RO-05 reading: FR-RO-05 is satisfied by UC-06 path (a),
  where a Scope Admin creates a person in their scope with `RoleId = User`; UC-08 keeps role changes
  System-Admin-only.
- Extend the alternative flow table:

| ID | Condition | Outcome |
| ---- | ----------- | --------- |
| AF-08a | Person not found or logically deleted | Return `404 Not Found` |
| AF-08b | New email conflicts within scope or system-wide | Return `409 Conflict` |
| AF-08c | Unauthorized role change (only System Admin may change `RoleId`) | Return `403 Forbidden` |
| AF-08d | Actor not authorized to update the person at all | Return `403 Forbidden` |
| AF-08e | Invalid input | Return `400 Bad Request` |
| AF-08f | Role change to a role that would require a target scope | Return `400 Bad Request` |
| AF-08g | Role change would leave a scope with no owner (NFR-12) | Return `409 Conflict` |

- [ ] **Step 2: Commit**

```bash
git add -A
git commit -m "docs: align UC-08 spec with implemented behavior"
```

---

## Definition of Done check (Development Workflow Document §5)

- [ ] Implemented on `feature/uc-08-update-person`, branched from an up-to-date `main`.
- [ ] Main flow and every alternative flow implemented.
- [ ] Unit tests cover the handler and the validator (main + applicable `AF-xx`).
- [ ] Functional tests cover the endpoint (main + every `AF-xx`, including authorization).
- [ ] `dotnet test … --filter "Category=Unit"` and `"Category=Functional"` both pass — real output read.
- [ ] Pull request opened into `main` with `Closes #9`, awaiting human review.
