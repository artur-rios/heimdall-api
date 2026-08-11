# UC-16: Create Application — Implementation Plan

Design: [2026-07-31-uc-16-create-application-design.md](../specs/2026-07-31-uc-16-create-application-design.md)
Issue: [#17](https://github.com/artur-rios/heimdall-api/issues/17)
Branch: `feature/uc-16-create-application`

Sequenced test-first per the
[Testing Specification Document](../../requirements/Testing%20Specification%20Document.md) §9.

## Step 0 — Messages and status map

- New `…Shared/Messages/ApplicationMessages.cs` (Decision 7):
  `ApplicationCreatedSuccessfully` (`"Application created successfully."`),
  `ScopeNotFound` (`"Scope not found."`),
  `NotScopeOwner` (`"You are not an owner of the target scope."`),
  `CannotSetAnotherOwner` (`"You may only create applications you own."`),
  `OwnerNotValidForScope` (`"Owner is not a valid member or owner of the scope."`),
  `NameRequired` (`"Application name is required."`),
  `NameTooLong` (`"Application name must be at most 200 characters."`),
  `OwnerRequired` (`"Owner is required."`).
- New `…Shared/Messages/ApplicationMessageMap.cs`: created → 201, `ScopeNotFound` → 404,
  `NotScopeOwner` / `CannotSetAnotherOwner` → 403, `OwnerNotValidForScope` → 400, the three
  validation messages → 400.
- Verify: `dotnet build src/ArturRios.Heimdall.sln` clean.

## Step 1 — Command, validator, output

- `CreateApplicationCommand : BaseCommand, IActorScoped` — `ScopeId` (route), `Name`, `OwnerId`
  (body), `ActingPersonId` / `ActingRole` (token).
- `CreateApplicationCommandValidator` — `Name` not empty / max 200; `OwnerId` not `Guid.Empty`
  (AF-16d).
- `CreateApplicationCommandOutput : CommandOutput` — `Id`, `Name`, `ScopeId`, `OwnerId`, `CreatedAt`,
  all public identifiers (Decision 8).

## Step 2 — Validator tests (red → green with Step 1)

`tests/Application/…Command.Tests/CreateApplicationCommandValidatorTests.cs`, mirroring
`CreateUserCommandValidatorTests`.

| Test | Covers |
| --- | --- |
| `GivenValidCommand_WhenValidating_ThenNoErrors` | AF-16d boundary |
| `GivenEmptyName_WhenValidating_ThenNameRequiredIsReported` | AF-16d |
| `GivenNameOf201Characters_WhenValidating_ThenNameTooLongIsReported` | AF-16d |
| `GivenNameOf200Characters_WhenValidating_ThenNoErrors` | AF-16d boundary |
| `GivenEmptyOwnerId_WhenValidating_ThenOwnerRequiredIsReported` | AF-16d |

## Step 3 — Handler tests (red)

`tests/Application/…Command.Tests/CreateApplicationCommandHandlerTests.cs`, mirroring
`CreateUserCommandHandlerTests`: `AsyncFakeRepository<Scope>` / `<Person>` / `<Application>`, a Moq
`IValidator<CreateApplicationCommand>`, and a Moq `IScopeOwnershipChecker`.

| Test | Covers |
| --- | --- |
| `GivenSystemAdminAndScopeUserOwner_WhenHandlingCreateApplication_ThenApplicationIsCreated` | main flow, FR-AP-03 leg 1 |
| `GivenSystemAdminAndScopeOwnerOwner_WhenHandlingCreateApplication_ThenApplicationIsCreated` | main flow, FR-AP-03 leg 2 |
| `GivenOwningScopeAdmin_WhenHandlingCreateApplication_ThenApplicationIsCreated` | main flow, matrix row |
| `GivenUserNamingThemself_WhenHandlingCreateApplication_ThenApplicationIsCreated` | main flow, matrix row |
| `GivenCreatedApplication_WhenHandlingCreateApplication_ThenRowCarriesScopeAndOwnerInternalIds` | UC-16 step 5, Decision 9 |
| `GivenMissingScope_WhenHandlingCreateApplication_ThenScopeNotFoundIsReported` | AF-16a |
| `GivenLogicallyDeletedScope_WhenHandlingCreateApplication_ThenScopeNotFoundIsReported` | AF-16a |
| `GivenUserNamingAnotherPerson_WhenHandlingCreateApplication_ThenCannotSetAnotherOwnerIsReported` | AF-16c |
| `GivenUserNamingANonExistentPerson_WhenHandlingCreateApplication_ThenCannotSetAnotherOwnerIsReported` | AF-16c, Decision 3 |
| `GivenScopeAdminWhoDoesNotOwnTheScope_WhenHandlingCreateApplication_ThenNotScopeOwnerIsReported` | Decision 2 |
| `GivenUnknownOwner_WhenHandlingCreateApplication_ThenOwnerNotValidIsReported` | AF-16b |
| `GivenLogicallyDeletedOwner_WhenHandlingCreateApplication_ThenOwnerNotValidIsReported` | AF-16b |
| `GivenOwnerOfADifferentScope_WhenHandlingCreateApplication_ThenOwnerNotValidIsReported` | AF-16b |
| `GivenSystemAdminAsOwner_WhenHandlingCreateApplication_ThenOwnerNotValidIsReported` | AF-16b, Decision 6 |
| `GivenInvalidInput_WhenHandlingCreateApplication_ThenNothingIsCreated` | AF-16d |

## Step 4 — `CreateApplicationCommandHandler` (green)

Per the design's handler-flow table. Dependencies: `IValidator<CreateApplicationCommand>`,
`IAsyncReadOnlyRepository<Scope>`, `IAsyncReadOnlyRepository<Person>`,
`IAsyncRepository<Application>`, `IScopeOwnershipChecker`. Returns `DataOutput<…>`, never throws;
each step commented with the UC/AF it implements, as the other handlers are.

- Verify: `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"` green.

## Step 5 — Endpoint and DI

- New `ApplicationController` — `[Route("api/scopes/{scopeId:guid}/applications")]`, with
  `Create` as `[HttpPost]`, **no** `[RoleRequirement]` (Decision 1). Binds `scopeId` from the route
  onto the command and calls `HttpContext.ApplyActor(command)`, as `PersonController.CreateUser`
  does. Resolves through `ApplicationMessageMap.StatusCodes`. XML doc naming UC-16, FR-AP-01/02/03,
  AF-16b/c, and why the action carries no role attribute.
- `Startup.AddDependencies`: register `IValidator<CreateApplicationCommand>` and
  `ICommandHandlerAsync<CreateApplicationCommand, CreateApplicationCommandOutput>`.

## Step 6 — Functional tests

`tests/Presentation/…WebApi.Tests/ApplicationControllerCreateTests.cs`, mirroring
`PersonControllerCreateUserTests`' seeding helpers (`SeedScopeAsync`, `SeedScopeAdminAsync`, plus a
`SeedUserAsync` that writes the `SCOPE_USER` row), authorised with
`TestTokens.For(person.PublicId, role)`.

| Test | Covers |
| --- | --- |
| `GivenSystemAdmin_WhenPostApplications_ThenApplicationIsCreated` | main flow + row assertions |
| `GivenOwnerScopeAdmin_WhenPostApplications_ThenApplicationIsCreated` | main flow, role |
| `GivenUserNamingThemself_WhenPostApplications_ThenApplicationIsCreated` | main flow, role |
| `GivenScopeAdminAsOwner_WhenPostApplications_ThenApplicationIsCreated` | FR-AP-03 leg 2 |
| `GivenMissingScope_WhenPostApplications_ThenNotFound` | AF-16a |
| `GivenLogicallyDeletedScope_WhenPostApplications_ThenNotFound` | AF-16a |
| `GivenScopeAdminNotOwner_WhenPostApplications_ThenForbidden` | Decision 2 |
| `GivenUserNamingAnotherPerson_WhenPostApplications_ThenForbiddenAndNothingIsCreated` | AF-16c |
| `GivenUnknownOwner_WhenPostApplications_ThenBadRequest` | AF-16b |
| `GivenOwnerOfADifferentScope_WhenPostApplications_ThenBadRequest` | AF-16b |
| `GivenUserOfADifferentScope_WhenPostApplications_ThenBadRequest` | Decision 4 |
| `GivenEmptyName_WhenPostApplications_ThenBadRequest` | AF-16d |
| `GivenNoToken_WhenPostApplications_ThenUnauthorized` | precondition |
| `GivenDuplicateName_WhenPostApplications_ThenBothAreCreated` | Decision 5 |

- Verify: `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional"` green.

## Step 7 — Documentation

- `Testing Specification Document.md` §10: add `CreateApplication` (+ its validator) to the
  Command.Tests row and `ApplicationController*` to the WebApi.Tests row; update the suite totals
  line to UC-16.
- `README.md`: mark UC-16 done in the tracker table.

## Step 8 — Full suite

`dotnet test src/ArturRios.Heimdall.sln` — both categories green before the pull request.
