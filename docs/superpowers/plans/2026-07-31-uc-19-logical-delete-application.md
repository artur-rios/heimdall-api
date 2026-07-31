# UC-19: Logical Delete Application — Implementation Plan

Design: [2026-07-31-uc-19-logical-delete-application-design.md](../specs/2026-07-31-uc-19-logical-delete-application-design.md)
Issue: [#20](https://github.com/artur-rios/identity-manager-api/issues/20)
Branch: `feature/uc-19-logical-delete-application`

Sequenced test-first per the
[Testing Specification Document](../../requirements/Testing%20Specification%20Document.md) §9.

---

## Step 1 — Messages and status map

- `ApplicationMessages` — add `ApplicationDeletedSuccessfully`
  (`"Application deleted successfully."`) and `NotAuthorizedToDeleteApplication`
  (`"You are not allowed to delete this application."`), each with a doc comment naming its flow.
- `ApplicationMessageMap` — `ApplicationDeletedSuccessfully` → 200,
  `NotAuthorizedToDeleteApplication` → 403. `ApplicationNotFound` is already mapped to 404 and is
  reused for AF-19a.

## Step 2 — Command and output

- `DeleteApplicationCommand : BaseCommand, IActorScoped` — `ScopeId`, `Id`, `ActingPersonId`,
  `ActingRole`. No validator (no body), mirroring `DeletePersonCommand`.
- `DeleteApplicationCommandOutput : CommandOutput` — `Id`, `AlreadyDeleted` (Decision 6), mirroring
  `DeletePersonCommandOutput`.

## Step 3 — Handler tests (red)

`tests/Application/…Command.Tests/DeleteApplicationCommandHandlerTests.cs`, reusing the seeding
helpers of `UpdateApplicationCommandHandlerTests` (`SeedScopeAsync`, `SeedScopeAdminAsync`,
`SeedApplicationAsync`) minus the ones only an owner change needs.

| Test | Covers |
| --- | --- |
| `GivenSystemAdmin_WhenHandlingDeleteApplication_ThenApplicationIsLogicallyDeleted` | main flow |
| `GivenOwningScopeAdmin_WhenHandlingDeleteApplication_ThenApplicationIsLogicallyDeleted` | main flow |
| `GivenActiveApplication_WhenHandlingDeleteApplication_ThenUpdatedAtIsStampedAndCreatedAtIsNot` | step 3 |
| `GivenOutput_WhenHandlingDeleteApplication_ThenItCarriesPublicIdentifiersOnly` | SRD §4.0 |
| `GivenAlreadyDeletedApplication_WhenHandlingDeleteApplication_ThenSuccessReportsAlreadyDeleted` | AF-19b |
| `GivenAlreadyDeletedApplication_WhenHandlingDeleteApplication_ThenNothingIsWritten` | AF-19b, Decision 7 |
| `GivenUnknownApplication_WhenHandlingDeleteApplication_ThenApplicationNotFoundIsReported` | AF-19a |
| `GivenApplicationOfADifferentScope_WhenHandlingDeleteApplication_ThenApplicationNotFoundIsReported` | AF-19a, Decision 4 |
| `GivenUnknownScope_WhenHandlingDeleteApplication_ThenApplicationNotFoundIsReported` | AF-19a, Decision 4 |
| `GivenScopeAdminWhoOwnsTheScopeButNotTheApplication_WhenHandlingDeleteApplication_ThenNotAuthorizedIsReported` | AF-19c, Decision 2 |
| `GivenUnrelatedScopeAdmin_WhenHandlingDeleteApplication_ThenNotAuthorizedIsReported` | AF-19c |
| `GivenNonOwnerAndAlreadyDeletedApplication_WhenHandlingDeleteApplication_ThenNotAuthorizedIsReported` | AF-19c over AF-19b, Decision 3 |

Every refusal test also asserts the stored row's `IsDeleted` is unchanged.

## Step 4 — Handler (green)

`DeleteApplicationCommandHandler` implementing the five-step flow from the design: load by
`PublicId` + `Scope.PublicId` in any deletion state → owner-or-System-Admin → already-deleted no-op →
flip `IsDeleted` and stamp `UpdatedAt` → return. Each step commented with the UC/AF it implements,
failures returned as errors on `DataOutput`.

- Verify: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"` green.

## Step 5 — Endpoint and DI

- `ApplicationController` — add `[HttpDelete("{id:guid}")] Delete(Guid scopeId, Guid id)` with
  `[RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]`, building the command from the
  route values, calling `HttpContext.ApplyActor(command)`, dispatching through `CommandMediator`, and
  resolving through `ApplicationMessageMap.StatusCodes`. XML doc naming UC-19, FR-AP-07, and
  AF-19a/b/c.
- `Startup.AddDependencies` — register
  `ICommandHandlerAsync<DeleteApplicationCommand, DeleteApplicationCommandOutput>`.

## Step 6 — Functional tests

`tests/Presentation/…WebApi.Tests/ApplicationControllerDeleteTests.cs`, reusing the seeding shape of
`ApplicationControllerUpdateTests`, authorised with `TestTokens.For(person.PublicId, role)`.

| Test | Covers |
| --- | --- |
| `GivenSystemAdmin_WhenDeleteApplication_ThenOkAndRowIsFlagged` | main flow + database state |
| `GivenOwningScopeAdmin_WhenDeleteApplication_ThenOkAndRowIsFlagged` | main flow |
| `GivenAlreadyDeletedApplication_WhenDeleteApplication_ThenOkAndNothingChanges` | AF-19b, Decision 7 |
| `GivenApplicationDeletedTwice_WhenDeleteApplication_ThenSecondCallReportsAlreadyDeleted` | AF-19b idempotency end to end |
| `GivenApplicationDeletedByItsScopeCascade_WhenDeleteApplication_ThenOkAndAlreadyDeleted` | AF-19b, Decision 5 |
| `GivenScopeAdminWhoOwnsTheScopeButNotTheApplication_WhenDeleteApplication_ThenForbidden` | AF-19c, Decision 2 |
| `GivenUserRole_WhenDeleteApplication_ThenForbidden` | AF-19c (attribute), Decision 11 |
| `GivenUnknownApplication_WhenDeleteApplication_ThenNotFound` | AF-19a |
| `GivenApplicationOfAnotherScope_WhenDeleteApplication_ThenNotFound` | AF-19a, Decision 4 |
| `GivenNoToken_WhenDeleteApplication_ThenUnauthorized` | precondition |

Refusal tests assert the persisted row is still active; success tests assert `is_deleted` and a moved
`updated_at`.

- Verify: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Functional"` green.

## Step 7 — Documentation

- `Testing Specification Document.md` §10: add `DeleteApplication` to the Command.Tests row, note
  `ApplicationControllerDeleteTests`, and update the suite totals line to UC-19.
- `README.md`: mark UC-19 ✅ in the use case tracker.

## Step 8 — Full suite

`dotnet test src/ArturRios.IdentityManager.sln` — both categories green before the pull request. The
pull request body references the use case and `Closes #20`.
