# UC-03: Update Scope — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a System Admin update an existing scope's name and description via `PUT /api/scopes/{id}` (UC-03 / FR-SC-04), mirroring the UC-01 write flow, with full unit and functional test coverage.

**Architecture:** New `UpdateScopeCommand` → `UpdateScopeCommandValidator` → `UpdateScopeCommandHandler` (loads the scope, rejects name collisions, applies changes, stamps `UpdatedAt`, persists via `IAsyncRepository<Scope>`) → `UpdateScopeCommandOutput`, exposed by a thin `PUT` action on `ScopeController` and wired in `Startup`. Handler failures are returned as errors on `DataOutput<T>` mapped to HTTP status codes via `ScopeMessageMap`. No schema change (no migration). Handler unit tests use `AsyncFakeRepository<Scope>` from `ArturRios.Util.Test` 2.2.0; functional tests run end-to-end against Testcontainers PostgreSQL, minting JWTs directly because UC-11 (Login) is not implemented.

**Tech Stack:** .NET 10, EF Core 10.0.10, FluentValidation 12.1.1, xUnit 2.9.3, ArturRios.Util.Test 2.2.0 (`AsyncFakeRepository`), Moq (latest stable), Bogus (latest stable), Testcontainers.PostgreSql 4.13.0, ArturRios.* first-party libraries.

## Global Constraints

- Target framework `net10.0`; `Nullable` and `ImplicitUsings` enabled (match existing projects).
- Inputs, outputs, and routes use `PublicId` (GUID) only — never expose or accept internal `bigint Id` (System Requirements §4.0 / NFR-15).
- Handlers return `DataOutput<T>` and never throw for expected failures; each alternative flow maps to a canonical `ScopeMessages` string, mapped to an HTTP status in `ScopeMessageMap`.
- Depend on `IAsyncReadOnlyRepository<Scope>` for reads and `IAsyncRepository<Scope>` for writes; query with `.Query()` + EF Core async LINQ (`AnyAsync`, `FirstOrDefaultAsync`, `ToListAsync`) — identical to `CreateScopeCommandHandler`.
- Controllers are thin: bind → dispatch via `CommandMediator` → `ResponseResolver.Resolve(result, statusMap: ScopeMessageMap.StatusCodes)`; authorization via `[RoleRequirement((int)Roles.SystemAdmin)]`.
- Every test is Given/When/Then in name and body (`// Given` → `// When` → `// Then`); unit tests use `[UnitFact]`, functional tests use `[FunctionalFact]` (from `ArturRios.Util.Test.Attributes`).
- **Do not modify any repository other than `heimdall-api`.** `AsyncFakeRepository` and its async-capable `Query()` are provided by the `ArturRios.Util.Test` 2.2.0 package (owned separately).
- PUT semantics: full replace — `Name` and `Description` come from the body; an omitted/null `description` clears the stored value.

**External dependency:** the handler unit tests (Task 2) can only be *run* once `ArturRios.Util.Test` **2.2.0 with an async-capable `AsyncFakeRepository.Query()`** is restorable from a configured NuGet source. The production code, the validator test (Task 1), and the functional tests (Tasks 3–5) do not depend on it, so they can be implemented and run independently.

---

## Task 1: Command input, validator, and output

The write-side contracts for UC-03. Mirrors `CreateScopeCommand` / `CreateScopeCommandValidator` / `CreateScopeCommandOutput`.

**Files:**
- Create: `src/Application/ArturRios.Heimdall.Command/Input/UpdateScopeCommand.cs`
- Create: `src/Application/ArturRios.Heimdall.Command/Input/Validation/UpdateScopeCommandValidator.cs`
- Create: `src/Application/ArturRios.Heimdall.Command/Output/UpdateScopeCommandOutput.cs`
- Test: `tests/Application/ArturRios.Heimdall.Command.Tests/UpdateScopeCommandValidatorTests.cs`

**Interfaces:**
- Produces: `UpdateScopeCommand { Guid Id; string Name; string? Description }` (`: BaseCommand`); `UpdateScopeCommandOutput { Guid Id; string Name; string? Description; bool GoogleSignInEnabled; IEnumerable<Guid> OwnerIds; DateTime CreatedAt; DateTime UpdatedAt }` (`: CommandOutput`); `UpdateScopeCommandValidator : AbstractValidator<UpdateScopeCommand>` (Name required).

- [ ] **Step 1: Write the failing validator test**

Create `UpdateScopeCommandValidatorTests.cs`:

```csharp
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using FluentValidation.TestHelper;

namespace ArturRios.Heimdall.Command.Tests;

public class UpdateScopeCommandValidatorTests
{
    private readonly UpdateScopeCommandValidator _validator = new();

    [UnitFact]
    public void GivenEmptyName_WhenValidating_ThenNameRequiredError()
    {
        // Given
        var command = new UpdateScopeCommand { Id = Guid.NewGuid(), Name = string.Empty };

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldHaveValidationErrorFor(x => x.Name).WithErrorMessage(ScopeMessages.NameRequired);
    }

    [UnitFact]
    public void GivenNonEmptyName_WhenValidating_ThenNoNameError()
    {
        // Given
        var command = new UpdateScopeCommand { Id = Guid.NewGuid(), Name = "Acme" };

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldNotHaveValidationErrorFor(x => x.Name);
    }
}
```

> Note: `FluentValidation.TestHelper` ships with the FluentValidation package that flows transitively from the `…Command` project reference. If the `TestValidate` extension is not resolved, add `<PackageReference Include="FluentValidation" Version="12.1.1" />` to the test csproj.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/ArturRios.Heimdall.sln --filter "FullyQualifiedName~UpdateScopeCommandValidatorTests"`
Expected: FAIL to compile — `UpdateScopeCommand` / `UpdateScopeCommandValidator` do not exist.

- [ ] **Step 3: Create the command, output, and validator**

`Input/UpdateScopeCommand.cs`:

```csharp
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to update an existing scope's name and description (UC-03). The scope is addressed by
///     its <c>PublicId</c> (GUID), bound from the route. PUT semantics: both <see cref="Name" /> and
///     <see cref="Description" /> are replaced; a null <see cref="Description" /> clears it.
/// </summary>
public class UpdateScopeCommand : BaseCommand
{
    /// <summary>Public identifier of the scope to update (bound from the route).</summary>
    public Guid Id { get; set; }

    /// <summary>New scope display name. Required and must be unique across all scopes.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>New description of the scope's purpose. Null clears any existing description.</summary>
    public string? Description { get; set; }
}
```

`Output/UpdateScopeCommandOutput.cs`:

```csharp
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     The scope as updated by <see cref="Input.UpdateScopeCommand" /> (UC-03). Only externally-facing
///     <c>PublicId</c> identifiers are exposed; internal Ids never leave the data layer.
/// </summary>
public class UpdateScopeCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the scope.</summary>
    public Guid Id { get; set; }

    /// <summary>Scope display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Scope description, if any.</summary>
    public string? Description { get; set; }

    /// <summary>Whether Google sign-in is enabled for the scope.</summary>
    public bool GoogleSignInEnabled { get; set; }

    /// <summary>Public identifiers of the scope's owners.</summary>
    public IEnumerable<Guid> OwnerIds { get; set; } = new List<Guid>();

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last update timestamp.</summary>
    public DateTime UpdatedAt { get; set; }
}
```

`Input/Validation/UpdateScopeCommandValidator.cs`:

```csharp
using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>
///     Input validation for <see cref="UpdateScopeCommand" /> (UC-03). Only checks the shape of the
///     request; business rules that require data access (existence, name uniqueness) are enforced by
///     the handler.
/// </summary>
public class UpdateScopeCommandValidator : AbstractValidator<UpdateScopeCommand>
{
    public UpdateScopeCommandValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .WithMessage(ScopeMessages.NameRequired);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/ArturRios.Heimdall.sln --filter "FullyQualifiedName~UpdateScopeCommandValidatorTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Command tests/Application/ArturRios.Heimdall.Command.Tests/UpdateScopeCommandValidatorTests.cs
git commit -m "feat: add UpdateScopeCommand input, validator, and output (UC-03)"
```

---

## Task 2: Handler + messages/status map (uses AsyncFakeRepository 2.2.0)

The business logic and its canonical messages. Reuses the existing `ScopeNotFound` (404) and `NameAlreadyExists` (409) messages; adds only the success message. Handler unit tests use `AsyncFakeRepository<Scope>` from `ArturRios.Util.Test` 2.2.0.

**Files:**
- Modify: `tests/Application/ArturRios.Heimdall.Command.Tests/ArturRios.Heimdall.Command.Tests.csproj`
- Modify: `src/Application/ArturRios.Heimdall.Shared/Messages/ScopeMessages.cs`
- Modify: `src/Application/ArturRios.Heimdall.Shared/Messages/ScopeMessageMap.cs`
- Create: `src/Application/ArturRios.Heimdall.Command/Handlers/UpdateScopeCommandHandler.cs`
- Test: `tests/Application/ArturRios.Heimdall.Command.Tests/UpdateScopeCommandHandlerTests.cs`
- (Docs) Modify: `docs/requirements/Technology Stack Document.md`

**Interfaces:**
- Consumes: `UpdateScopeCommand`, `UpdateScopeCommandOutput` (Task 1); `AsyncFakeRepository<Scope>` (ArturRios.Util.Test 2.2.0).
- Produces: `UpdateScopeCommandHandler(IValidator<UpdateScopeCommand>, IAsyncReadOnlyRepository<Scope>, IAsyncRepository<Scope>) : ICommandHandlerAsync<UpdateScopeCommand, UpdateScopeCommandOutput>`; `ScopeMessages.ScopeUpdatedSuccessfully`.

- [ ] **Step 1: Bump the test packages**

In `ArturRios.Heimdall.Command.Tests.csproj`, change the `ArturRios.Util.Test` version and add Moq + Bogus (leave the other entries untouched):

```xml
<PackageReference Include="ArturRios.Util.Test" Version="2.2.0" />
<PackageReference Include="Bogus" Version="35.6.3" />
<PackageReference Include="Moq" Version="4.20.72" />
```

(If a newer stable Moq/Bogus is current, use it — the Tech Stack doc mandates "latest stable" for both.)

Also update the pins in `docs/requirements/Technology Stack Document.md`: set `ArturRios.Util.Test` to `2.2.0` (note it now also provides `AsyncFakeRepository<T>`) in both the dependency table and the version-summary table, and replace the "to be pinned" Moq/Bogus rows with the versions you added.

- [ ] **Step 2: Add the success message and its status mapping**

In `ScopeMessages.cs`, add after `ScopeCreatedSuccessfully`:

```csharp
    /// <summary>UC-03 success: the scope was updated.</summary>
    public const string ScopeUpdatedSuccessfully = "Scope updated successfully.";
```

In `ScopeMessageMap.cs`, add inside the dictionary (after the `ScopeCreatedSuccessfully` entry):

```csharp
        // UC-03 main flow — scope updated.
        [ScopeMessages.ScopeUpdatedSuccessfully] = HttpStatusCodes.Ok,
```

(`ScopeNotFound` → NotFound and `NameAlreadyExists` → Conflict are already mapped and are reused for AF-03a / AF-03b.)

- [ ] **Step 3: Write the failing handler tests**

Create `UpdateScopeCommandHandlerTests.cs`:

```csharp
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Bogus;
using FluentValidation;
using FluentValidation.Results;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

public class UpdateScopeCommandHandlerTests
{
    private static Mock<IValidator<UpdateScopeCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<UpdateScopeCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<UpdateScopeCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator;
    }

    private static async Task<AsyncFakeRepository<Scope>> RepositoryWith(params Scope[] scopes)
    {
        var repository = new AsyncFakeRepository<Scope>();

        foreach (var scope in scopes)
        {
            await repository.CreateAsync(scope);
        }

        return repository;
    }

    private static Scope ExistingScope(Guid publicId, string name, Guid ownerPublicId, bool isDeleted = false)
    {
        // Bogus builds the owner person; only the fields the behavior depends on are pinned.
        var owner = new Faker<Person>()
            .RuleFor(p => p.PublicId, _ => ownerPublicId)
            .Generate();

        return new Scope
        {
            PublicId = publicId,
            Name = name,
            Description = "Original description",
            IsDeleted = isDeleted,
            Owners = [new ScopeOwner { Person = owner }]
        };
    }

    [UnitFact]
    public async Task GivenExistingScopeAndUniqueName_WhenHandlingUpdateScope_ThenScopeIsUpdated()
    {
        // Given
        var id = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var repository = await RepositoryWith(ExistingScope(id, "Old Name", ownerId));
        var handler = new UpdateScopeCommandHandler(ValidValidator().Object, repository, repository);
        var command = new UpdateScopeCommand { Id = id, Name = "New Name", Description = "New description" };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.Equal("New Name", output.Data!.Name);
        Assert.Equal("New description", output.Data.Description);
        Assert.Equal([ownerId], output.Data.OwnerIds);
        Assert.Contains(ScopeMessages.ScopeUpdatedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenNullDescription_WhenHandlingUpdateScope_ThenDescriptionIsCleared()
    {
        // Given
        var id = Guid.NewGuid();
        var repository = await RepositoryWith(ExistingScope(id, "Old Name", Guid.NewGuid()));
        var handler = new UpdateScopeCommandHandler(ValidValidator().Object, repository, repository);
        var command = new UpdateScopeCommand { Id = id, Name = "Old Name", Description = null };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Null(output.Data!.Description);
    }

    [UnitFact]
    public async Task GivenNameUnchanged_WhenHandlingUpdateScope_ThenNoFalseConflict()
    {
        // Given a scope keeping its own name
        var id = Guid.NewGuid();
        var repository = await RepositoryWith(ExistingScope(id, "Same Name", Guid.NewGuid()));
        var handler = new UpdateScopeCommandHandler(ValidValidator().Object, repository, repository);
        var command = new UpdateScopeCommand { Id = id, Name = "Same Name", Description = "Changed" };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal("Changed", output.Data!.Description);
    }

    [UnitFact]
    public async Task GivenMissingScope_WhenHandlingUpdateScope_ThenReturnsScopeNotFound()
    {
        // Given an empty store
        var repository = await RepositoryWith();
        var handler = new UpdateScopeCommandHandler(ValidValidator().Object, repository, repository);
        var command = new UpdateScopeCommand { Id = Guid.NewGuid(), Name = "New Name" };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedScope_WhenHandlingUpdateScope_ThenReturnsScopeNotFound()
    {
        // Given a scope that is logically deleted
        var id = Guid.NewGuid();
        var repository = await RepositoryWith(ExistingScope(id, "Old Name", Guid.NewGuid(), isDeleted: true));
        var handler = new UpdateScopeCommandHandler(ValidValidator().Object, repository, repository);
        var command = new UpdateScopeCommand { Id = id, Name = "New Name" };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenNameUsedByAnotherScope_WhenHandlingUpdateScope_ThenReturnsNameAlreadyExists()
    {
        // Given two scopes; the target will try to take the other's name
        var id = Guid.NewGuid();
        var repository = await RepositoryWith(
            ExistingScope(id, "Target", Guid.NewGuid()),
            ExistingScope(Guid.NewGuid(), "Taken", Guid.NewGuid()));
        var handler = new UpdateScopeCommandHandler(ValidValidator().Object, repository, repository);
        var command = new UpdateScopeCommand { Id = id, Name = "Taken" };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.NameAlreadyExists, output.Errors);
    }

    [UnitFact]
    public async Task GivenInvalidInput_WhenHandlingUpdateScope_ThenReturnsValidationError()
    {
        // Given a validator that reports a failure
        var validator = new Mock<IValidator<UpdateScopeCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<UpdateScopeCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([new ValidationFailure("Name", ScopeMessages.NameRequired)]));
        var repository = await RepositoryWith();
        var handler = new UpdateScopeCommandHandler(validator.Object, repository, repository);
        var command = new UpdateScopeCommand { Id = Guid.NewGuid(), Name = string.Empty };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.NameRequired, output.Errors);
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test src/ArturRios.Heimdall.sln --filter "FullyQualifiedName~UpdateScopeCommandHandlerTests"`
Expected: FAIL to compile — `UpdateScopeCommandHandler` does not exist. (If restore fails because `ArturRios.Util.Test` 2.2.0 with the async-capable `AsyncFakeRepository.Query()` is not yet published, pause here until it is available — see the external dependency note above.)

- [ ] **Step 5: Implement the handler**

Create `Handlers/UpdateScopeCommandHandler.cs`:

```csharp
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="UpdateScopeCommand" /> (UC-03): validates the request, verifies the scope
///     exists and is not logically deleted (AF-03a), verifies the new name does not collide with
///     another scope (AF-03b), then applies the changes and stamps <c>UpdatedAt</c>. All failures are
///     returned as errors on the <see cref="DataOutput{T}" /> rather than thrown, using the canonical
///     <see cref="ScopeMessages" /> so the response resolver can pick the matching status code.
/// </summary>
public class UpdateScopeCommandHandler(
    IValidator<UpdateScopeCommand> validator,
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncRepository<Scope> scopeWriter)
    : ICommandHandlerAsync<UpdateScopeCommand, UpdateScopeCommandOutput>
{
    public async Task<DataOutput<UpdateScopeCommandOutput?>> HandleAsync(UpdateScopeCommand command)
    {
        var output = DataOutput<UpdateScopeCommandOutput?>.New;

        // Step 2: validate input fields.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // Step 3 (AF-03a): the scope must exist and not be logically deleted.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.Id && !x.IsDeleted);

        if (scope is null)
        {
            return output.WithError(ScopeMessages.ScopeNotFound);
        }

        // Step 4 (AF-03b): the new name must not collide with another scope. Checked against all
        // scopes (deleted included) to match the unique index on Name, excluding this scope so an
        // unchanged name is not a false conflict.
        var nameTaken = await scopeReader.Query()
            .AnyAsync(x => x.Name == command.Name && x.PublicId != command.Id);

        if (nameTaken)
        {
            return output.WithError(ScopeMessages.NameAlreadyExists);
        }

        // Owner PublicIds for the response. A projection over the navigation (no Include needed),
        // which EF translates to a join and the in-memory fake evaluates directly.
        var ownerIds = await scopeReader.Query()
            .Where(x => x.PublicId == command.Id)
            .SelectMany(x => x.Owners.Select(owner => owner.Person.PublicId))
            .ToListAsync();

        // Step 4 (main flow): apply the updates and stamp UpdatedAt (no DB trigger maintains it).
        scope.Name = command.Name;
        scope.Description = command.Description;
        scope.UpdatedAt = DateTime.UtcNow;

        var update = await scopeWriter.UpdateAsync(scope);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        // Step 5: return the updated scope.
        return output
            .WithData(new UpdateScopeCommandOutput
            {
                Id = scope.PublicId,
                Name = scope.Name,
                Description = scope.Description,
                GoogleSignInEnabled = scope.GoogleSignInEnabled,
                OwnerIds = ownerIds,
                CreatedAt = scope.CreatedAt,
                UpdatedAt = scope.UpdatedAt
            })
            .WithMessage(ScopeMessages.ScopeUpdatedSuccessfully);
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test src/ArturRios.Heimdall.sln --filter "FullyQualifiedName~UpdateScopeCommandHandlerTests"`
Expected: PASS (7 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Shared src/Application/ArturRios.Heimdall.Command/Handlers "docs/requirements/Technology Stack Document.md" tests/Application/ArturRios.Heimdall.Command.Tests
git commit -m "feat: add UpdateScopeCommandHandler with messages and status map (UC-03)"
```

---

## Task 3: Controller endpoint + DI wiring

Exposes the use case and registers it. Deliverable: the whole solution builds and the endpoint is wired and authorized.

**Files:**
- Modify: `src/Presentation/ArturRios.Heimdall.WebApi/Controllers/ScopeController.cs`
- Modify: `src/Presentation/ArturRios.Heimdall.WebApi/Startup.cs`

**Interfaces:**
- Consumes: `UpdateScopeCommand`, `UpdateScopeCommandOutput`, `UpdateScopeCommandHandler`, `UpdateScopeCommandValidator`.
- Produces: `PUT /api/scopes/{id}` action; DI registrations for the validator and command handler.

- [ ] **Step 1: Add the PUT action to `ScopeController`**

The file already imports `Command.Input`, `Command.Output`, `Domain.Enums`, `Shared.Messages`, `Mediator.Command`, `Output`, `Util.WebApi.AspNetCore`, `Util.WebApi.Security.Attributes`, `Microsoft.AspNetCore.Mvc`. Insert this action after `Create`:

```csharp
    /// <summary>
    ///     Updates an existing scope's name and description (UC-03). Restricted to System Admins.
    /// </summary>
    [HttpPut("{id:guid}")]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<DataOutput<UpdateScopeCommandOutput?>>> Update(
        Guid id, [FromBody] UpdateScopeCommand command)
    {
        command.Id = id;

        var result = await commandMediator
            .ExecuteCommandAsync<UpdateScopeCommand, UpdateScopeCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: ScopeMessageMap.StatusCodes);
    }
```

- [ ] **Step 2: Register the handler and validator in `Startup.AddDependencies`**

`Startup.cs` already imports `ArturRios.Heimdall.Command.Handlers` and `ArturRios.Heimdall.Command.Input.Validation`. Add these registrations right after the two `CreateScopeCommand` registrations:

```csharp
        Builder.Services.AddScoped<IValidator<UpdateScopeCommand>, UpdateScopeCommandValidator>();
        Builder.Services
            .AddScoped<ICommandHandlerAsync<UpdateScopeCommand, UpdateScopeCommandOutput>, UpdateScopeCommandHandler>();
```

- [ ] **Step 3: Build the solution**

Run: `dotnet build src/ArturRios.Heimdall.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Presentation/ArturRios.Heimdall.WebApi
git commit -m "feat: expose PUT /api/scopes/{id} and register UC-03 handler"
```

---

## Task 4: Functional test infrastructure + main-flow test

Establishes the authenticated-functional-test pattern (mint the app JWT directly, since UC-11 Login is not implemented) and proves the endpoint end-to-end against Testcontainers PostgreSQL.

**Files:**
- Modify: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ArturRios.Heimdall.WebApi.Tests.csproj`
- Create: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/Support/TestTokens.cs`
- Create: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ScopeControllerUpdateTests.cs`

**Interfaces:**
- Consumes: `PostgresFixture` (existing), `FunctionalCollection` (existing), `WebApiTest<Program>.Authorize(string)` and `.Gateway`.
- Produces: `TestTokens.ForRole(int role)` → a signed app JWT carrying `id`/`role` claims valid for the host under test; `ScopeControllerUpdateTests` functional class.

- [ ] **Step 1: Align the test package version**

In `ArturRios.Heimdall.WebApi.Tests.csproj`, bump `ArturRios.Util.Test` to keep one version across the solution:

```xml
<PackageReference Include="ArturRios.Util.Test" Version="2.2.0" />
```

- [ ] **Step 2: Write the token helper**

Create `Support/TestTokens.cs`:

```csharp
using ArturRios.Jwt;
using ArturRios.Util.WebApi.Security.Extensions;
using ArturRios.Util.WebApi.Security.Records;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     Mints the application's own HMAC JWT directly for functional tests. UC-11 (Login) is not yet
///     implemented, so there is no auth route to exchange credentials at; tests craft a token with the
///     required <c>id</c>/<c>role</c> claims, signed with the same secret/issuer/audience the host
///     under test validates against (published into the environment by <see cref="PostgresFixture" />).
/// </summary>
public static class TestTokens
{
    private const string SecretVariable = "HEIMDALL_AUTH_TOKEN_SECRET";
    private const string IssuerVariable = "HEIMDALL_AUTH_TOKEN_ISSUER";
    private const string AudienceVariable = "HEIMDALL_AUTH_TOKEN_AUDIENCE";

    /// <summary>Builds a bearer token for a user with the given role value (see <c>Roles</c>).</summary>
    public static string ForRole(int role)
    {
        var claims = new AuthenticatedUser(1, role).ToTokenClaims();

        var configuration = new JwtConfiguration(
            3600,
            Environment.GetEnvironmentVariable(IssuerVariable) ?? string.Empty,
            Environment.GetEnvironmentVariable(AudienceVariable) ?? string.Empty,
            Environment.GetEnvironmentVariable(SecretVariable) ?? string.Empty,
            claims);

        return new JwtHandler().CreateToken(configuration);
    }
}
```

- [ ] **Step 3: Write the failing main-flow functional test**

Create `ScopeControllerUpdateTests.cs`:

```csharp
using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
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
public class ScopeControllerUpdateTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string UniqueName() => $"scope-{Guid.NewGuid():N}";

    private async Task<Scope> SeedScopeAsync(string name, bool isDeleted = false)
    {
        await using var context = db.CreateContext();

        var scope = new Scope { PublicId = Guid.NewGuid(), Name = name, Description = "Original", IsDeleted = isDeleted };

        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        return scope;
    }

    [FunctionalFact]
    public async Task GivenSystemAdminAndValidPayload_WhenPutScope_ThenScopeIsUpdated()
    {
        // Given
        var scope = await SeedScopeAsync(UniqueName());
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var newName = UniqueName();

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}",
            new UpdateScopeCommand { Name = newName, Description = "Updated" });

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(newName, response.Body?.Data?.Name);
        Assert.Equal("Updated", response.Body?.Data?.Description);

        // Then — database state
        await using var context = db.CreateContext();
        var persisted = await context.Scopes.AsNoTracking().FirstAsync(x => x.PublicId == scope.PublicId);
        Assert.Equal(newName, persisted.Name);
        Assert.Equal("Updated", persisted.Description);
    }
}
```

> Verify the `WebApiTest<Program>` and attribute namespaces against `HealthCheckTests.cs` before running (the base class and `[FunctionalFact]` live in `ArturRios.Util.Test.Functional` / `ArturRios.Util.Test.Attributes`); adjust the `using`s to match that file if it differs.

- [ ] **Step 4: Run test to verify it fails, then make it pass**

Run: `dotnet test src/ArturRios.Heimdall.sln --filter "FullyQualifiedName~ScopeControllerUpdateTests"`
Expected: FAIL until `TestTokens` / the test compile; then PASS. The production code from Tasks 1–3 already implements the behavior — no production change should be needed for the main flow. If it fails at runtime, debug per `superpowers:systematic-debugging` (common causes: claim/secret mismatch → 401; wrong `WebApiTest`/attribute namespace → compile error).

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test src/ArturRios.Heimdall.sln --filter "FullyQualifiedName~ScopeControllerUpdateTests"`
Expected: PASS (1 test).

- [ ] **Step 6: Commit**

```bash
git add tests/Presentation/ArturRios.Heimdall.WebApi.Tests
git commit -m "test: add functional token helper and UC-03 main-flow test"
```

---

## Task 5: Functional alternative-flow and authorization tests

Covers every remaining UC-03 flow at the API boundary: AF-03a (missing / deleted → 404), AF-03b (name conflict → 409), invalid input (empty name → 400), and authorization (non-System-Admin → 403, unauthenticated → 401).

**Files:**
- Modify: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ScopeControllerUpdateTests.cs`

- [ ] **Step 1: Add the alternative-flow tests**

Append these methods to `ScopeControllerUpdateTests`:

```csharp
    [FunctionalFact]
    public async Task GivenUnknownScopeId_WhenPutScope_ThenNotFound()
    {
        // Given
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopeCommandOutput?>>(
            $"/api/scopes/{Guid.NewGuid()}",
            new UpdateScopeCommand { Name = UniqueName() });

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedScope_WhenPutScope_ThenNotFound()
    {
        // Given
        var scope = await SeedScopeAsync(UniqueName(), isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}",
            new UpdateScopeCommand { Name = UniqueName() });

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNameOfAnotherScope_WhenPutScope_ThenConflict()
    {
        // Given two scopes; the target tries to take the other's name
        var target = await SeedScopeAsync(UniqueName());
        var other = await SeedScopeAsync(UniqueName());
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopeCommandOutput?>>(
            $"/api/scopes/{target.PublicId}",
            new UpdateScopeCommand { Name = other.Name });

        // Then — response
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // Then — target's name is unchanged in the database
        await using var context = db.CreateContext();
        var persisted = await context.Scopes.AsNoTracking().FirstAsync(x => x.PublicId == target.PublicId);
        Assert.Equal(target.Name, persisted.Name);
    }

    [FunctionalFact]
    public async Task GivenEmptyName_WhenPutScope_ThenBadRequest()
    {
        // Given
        var scope = await SeedScopeAsync(UniqueName());
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}",
            new UpdateScopeCommand { Name = string.Empty });

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNonSystemAdmin_WhenPutScope_ThenForbidden()
    {
        // Given
        var scope = await SeedScopeAsync(UniqueName());
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}",
            new UpdateScopeCommand { Name = UniqueName() });

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPutScope_ThenUnauthorized()
    {
        // Given a scope but no Authorize call (no bearer token on the gateway)
        var scope = await SeedScopeAsync(UniqueName());

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}",
            new UpdateScopeCommand { Name = UniqueName() });

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
```

- [ ] **Step 2: Run to verify they pass**

Run: `dotnet test src/ArturRios.Heimdall.sln --filter "FullyQualifiedName~ScopeControllerUpdateTests"`
Expected: all 7 tests PASS. If 403/401 return the wrong code, confirm the `[RoleRequirement]` attribute and that the unauthenticated test issues no token; debug per `superpowers:systematic-debugging`. No production change is expected beyond what Tasks 1–3 built.

- [ ] **Step 3: Run the full suite**

Run:
```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional"
```
Expected: both green.

- [ ] **Step 4: Commit**

```bash
git add tests/Presentation/ArturRios.Heimdall.WebApi.Tests/ScopeControllerUpdateTests.cs
git commit -m "test: cover UC-03 alternative and authorization flows end-to-end"
```

---

## Definition of Done (mirrors the workflow doc)

- [ ] Implemented on `feature/uc-03-update-scope` from `main`.
- [ ] Main flow + AF-03a + AF-03b implemented; validation and authorization enforced.
- [ ] Unit tests cover the handler (main, null-description, unchanged-name, AF-03a ×2, AF-03b, invalid input) and the validator; functional tests cover the endpoint (main + AF-03a ×2 + AF-03b + 400 + 403 + 401), asserting response and DB state.
- [ ] `Category=Unit` and `Category=Functional` both pass.
- [ ] PR reviewed by a human and merged; branch deleted; issue in **Done** and closed.

## Notes for the executor

- No EF migration: `Name`, `Description`, and the unique `Name` index already exist in `ScopeDbMap` / the initial migration.
- Handler unit tests depend on `ArturRios.Util.Test` 2.2.0 with an async-capable `AsyncFakeRepository.Query()`; the production code, validator test, and functional tests do not. If 2.2.0 is not yet restorable, implement Tasks 1, 3, 4, 5 and the production half of Task 2 first, and run the handler unit tests once the package is available.
- The functional suite shares one PostgreSQL container and does not reset between tests, so every test uses `UniqueName()` / fresh `Guid` PublicIds to stay independent.
- Timestamps: `CreatedAt`/`UpdatedAt` have `HasDefaultValueSql("now()")`; the handler sets `UpdatedAt = DateTime.UtcNow` on update because no DB trigger maintains it.
