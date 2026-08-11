# UC-18: Update Application — Implementation Plan

Design: [2026-07-31-uc-18-update-application-design.md](../specs/2026-07-31-uc-18-update-application-design.md)
Issue: [#19](https://github.com/artur-rios/heimdall-api/issues/19)
Branch: `feature/uc-18-update-application`

Sequenced test-first per the
[Testing Specification Document](../../requirements/Testing%20Specification%20Document.md) §9.

---

## Step 1 — Messages and status map

- `ApplicationMessages` — add `ApplicationUpdatedSuccessfully`
  (`"Application updated successfully."`) and `NotAuthorizedToUpdateApplication`
  (`"You are not allowed to update this application."`), each with a doc comment naming its flow.
- `ApplicationMessageMap` — `ApplicationUpdatedSuccessfully` → 200,
  `NotAuthorizedToUpdateApplication` → 403. `ApplicationNotFound`, `OwnerNotValidForScope`, and the
  three input messages are already mapped and are reused as-is.

## Step 2 — Command, validator, output

- `UpdateApplicationCommand : BaseCommand, IActorScoped` — `ScopeId`, `Id`, `Name`, `OwnerId`,
  `ActingPersonId`, `ActingRole`.
- `UpdateApplicationCommandValidator` — `Name` not empty (`NameRequired`) and max 200
  (`NameTooLong`); `OwnerId` not empty (`OwnerRequired`).
- `UpdateApplicationCommandOutput : CommandOutput` — `Id`, `Name`, `ScopeId`, `OwnerId`,
  `CreatedAt`, `UpdatedAt` (Decision 11).

## Step 3 — Validator tests (red)

`tests/Application/…Command.Tests/UpdateApplicationCommandValidatorTests.cs`, mirroring
`UpdateScopeCommandValidatorTests`.

| Test | Covers |
| --- | --- |
| `GivenValidCommand_WhenValidating_ThenValidationPasses` | step 2 |
| `GivenEmptyName_WhenValidating_ThenNameRequiredIsReported` | step 2 |
| `GivenNameLongerThanMaximum_WhenValidating_ThenNameTooLongIsReported` | step 2 |
| `GivenNameAtMaximumLength_WhenValidating_ThenValidationPasses` | boundary |
| `GivenEmptyOwnerId_WhenValidating_ThenOwnerRequiredIsReported` | step 2 |

## Step 4 — Handler tests (red)

`tests/Application/…Command.Tests/UpdateApplicationCommandHandlerTests.cs`, reusing the seeding
helpers' shape from `CreateApplicationCommandHandlerTests` plus a `SeedApplicationAsync`.

| Test | Covers |
| --- | --- |
| `GivenSystemAdmin_WhenHandlingUpdateApplication_ThenApplicationIsUpdated` | main flow |
| `GivenOwningScopeAdmin_WhenHandlingUpdateApplication_ThenApplicationIsUpdated` | main flow |
| `GivenUpdatedApplication_WhenHandlingUpdateApplication_ThenUpdatedAtIsStampedAndCreatedAtIsNot` | step 5 |
| `GivenOutput_WhenHandlingUpdateApplication_ThenItCarriesPublicIdentifiers` | SRD §4.0 |
| `GivenOwningScopeAdminTransferringToACoOwner_WhenHandlingUpdateApplication_ThenOwnerChanges` | Decision 1 |
| `GivenUnchangedOwnerWhoIsNowLogicallyDeleted_WhenHandlingUpdateApplication_ThenApplicationIsUpdated` | Decision 6 |
| `GivenUnknownApplication_WhenHandlingUpdateApplication_ThenApplicationNotFoundIsReported` | AF-18a |
| `GivenApplicationOfADifferentScope_WhenHandlingUpdateApplication_ThenApplicationNotFoundIsReported` | AF-18a, Decision 3 |
| `GivenUnknownScope_WhenHandlingUpdateApplication_ThenApplicationNotFoundIsReported` | AF-18a, Decision 3 |
| `GivenLogicallyDeletedApplication_WhenHandlingUpdateApplication_ThenApplicationNotFoundIsReported` | AF-18a, Decision 8 |
| `GivenScopeAdminWhoOwnsTheScopeButNotTheApplication_WhenHandlingUpdateApplication_ThenNotAuthorizedIsReported` | AF-18c, Decision 2 |
| `GivenUnrelatedScopeAdmin_WhenHandlingUpdateApplication_ThenNotAuthorizedIsReported` | AF-18c |
| `GivenUnknownNewOwner_WhenHandlingUpdateApplication_ThenOwnerNotValidIsReported` | AF-18b |
| `GivenLogicallyDeletedNewOwner_WhenHandlingUpdateApplication_ThenOwnerNotValidIsReported` | AF-18b |
| `GivenNewOwnerWithUserRole_WhenHandlingUpdateApplication_ThenOwnerNotValidIsReported` | AF-18b, FR-AP-03 |
| `GivenNewOwnerWhoIsASystemAdmin_WhenHandlingUpdateApplication_ThenOwnerNotValidIsReported` | AF-18b, FR-AP-03 |
| `GivenNewOwnerScopeAdminOfADifferentScope_WhenHandlingUpdateApplication_ThenOwnerNotValidIsReported` | AF-18b |
| `GivenInvalidInput_WhenHandlingUpdateApplication_ThenNothingIsChanged` | step 2 |

Every refusal test also asserts the stored row still carries its original `Name` / `OwnerId`.

## Step 5 — Handler (green)

`UpdateApplicationCommandHandler` implementing the six-step flow from the design: validate → load by
`PublicId` + `Scope.PublicId` + `!IsDeleted` → owner-or-System-Admin → conditional new-owner
eligibility → apply and stamp `UpdatedAt` → return. Each step commented with the UC/AF it implements,
failures returned as errors on `DataOutput`.

- Verify: `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"` green.

## Step 6 — Endpoint and DI

- `ApplicationController` — add `[HttpPut("{id:guid}")] Update(Guid scopeId, Guid id, [FromBody]
  UpdateApplicationCommand command)` with
  `[RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]`, binding the route values,
  calling `HttpContext.ApplyActor(command)`, dispatching through `CommandMediator`, and resolving
  through `ApplicationMessageMap.StatusCodes`. XML doc naming UC-18, FR-AP-06, and AF-18a/b/c.
- `Startup.AddDependencies` — register `IValidator<UpdateApplicationCommand>` and
  `ICommandHandlerAsync<UpdateApplicationCommand, UpdateApplicationCommandOutput>`.

## Step 7 — Functional tests

`tests/Presentation/…WebApi.Tests/ApplicationControllerUpdateTests.cs`, reusing the seeding shape of
`ApplicationControllerGetByIdTests`, authorised with `TestTokens.For(person.PublicId, role)`.

| Test | Covers |
| --- | --- |
| `GivenSystemAdmin_WhenPutApplication_ThenOkAndRowIsUpdated` | main flow + database state |
| `GivenOwningScopeAdmin_WhenPutApplication_ThenOkAndRowIsUpdated` | main flow |
| `GivenOwningScopeAdminTransferringToACoOwner_WhenPutApplication_ThenOkAndOwnerRowMoves` | Decision 1 |
| `GivenScopeAdminWhoOwnsTheScopeButNotTheApplication_WhenPutApplication_ThenForbidden` | AF-18c, Decision 2 |
| `GivenUserRole_WhenPutApplication_ThenForbidden` | AF-18c (attribute) |
| `GivenUnknownApplication_WhenPutApplication_ThenNotFound` | AF-18a |
| `GivenApplicationOfAnotherScope_WhenPutApplication_ThenNotFound` | AF-18a, Decision 3 |
| `GivenLogicallyDeletedApplication_WhenPutApplication_ThenNotFound` | AF-18a, Decision 8 |
| `GivenNewOwnerWithUserRole_WhenPutApplication_ThenBadRequest` | AF-18b, FR-AP-03 |
| `GivenNewOwnerOfADifferentScope_WhenPutApplication_ThenBadRequest` | AF-18b |
| `GivenUnknownNewOwner_WhenPutApplication_ThenBadRequest` | AF-18b |
| `GivenEmptyName_WhenPutApplication_ThenBadRequest` | step 2 |
| `GivenForgedActingRoleInBody_WhenPutApplication_ThenItIsIgnored` | `ApplyActor` |
| `GivenNoToken_WhenPutApplication_ThenUnauthorized` | precondition |

Refusal tests assert the persisted row is unchanged; success tests assert `name` / `owner_id` and a
moved `updated_at`.

- Verify: `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional"` green.

## Step 8 — Documentation

- `Testing Specification Document.md` §10: add `UpdateApplication` to the Command.Tests row, note
  `ApplicationControllerUpdateTests`, and update the suite totals line to UC-18.
- `README.md`: mark UC-18 ✅ in the use case tracker.

## Step 9 — Full suite

`dotnet test src/ArturRios.Heimdall.sln` — both categories green before the pull request. The
pull request body references the use case and `Closes #19`.
