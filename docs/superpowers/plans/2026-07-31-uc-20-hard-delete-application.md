# UC-20: Hard Delete Application — Implementation Plan

Design: [2026-07-31-uc-20-hard-delete-application-design.md](../specs/2026-07-31-uc-20-hard-delete-application-design.md)
Issue: [#21](https://github.com/artur-rios/identity-manager-api/issues/21)
Branch: `feature/uc-20-hard-delete-application`

Sequenced test-first per the
[Testing Specification Document](../../requirements/Testing%20Specification%20Document.md) §9.

---

## Step 1 — Messages and status map

- `ApplicationMessages` — add `ApplicationHardDeletedSuccessfully`
  (`"Application hard deleted successfully."`) with a doc comment naming UC-20.
- `ApplicationMessageMap` — `ApplicationHardDeletedSuccessfully` → 200. `ApplicationNotFound` is
  already mapped to 404 and is reused for AF-20a.

## Step 2 — Command and output

- `HardDeleteApplicationCommand : BaseCommand` — `ScopeId`, `Id`. No `IActorScoped` (Decision 2) and
  no validator (no body), mirroring `HardDeleteScopeCommand`.
- `HardDeleteApplicationCommandOutput : CommandOutput` — `Id` only; nothing cascades, so there is no
  dependent total to report (Decisions 5 and 8).

## Step 3 — Handler tests (red)

`tests/Application/…Command.Tests/HardDeleteApplicationCommandHandlerTests.cs`, reusing the seeding
helpers of `DeleteApplicationCommandHandlerTests` minus the ones only an authorization check needs.

| Test | Covers |
| --- | --- |
| `GivenSystemAdmin_WhenHandlingHardDeleteApplication_ThenApplicationIsRemoved` | main flow |
| `GivenLogicallyDeletedApplication_WhenHandlingHardDeleteApplication_ThenApplicationIsRemoved` | Decision 1 |
| `GivenOutput_WhenHandlingHardDeleteApplication_ThenItCarriesPublicIdentifiersOnly` | SRD §4.0, Decision 8 |
| `GivenSiblingApplicationInTheSameScope_WhenHandlingHardDeleteApplication_ThenOnlyTheAddressedOneIsRemoved` | main flow scoping |
| `GivenUnknownApplication_WhenHandlingHardDeleteApplication_ThenApplicationNotFoundIsReported` | AF-20a |
| `GivenApplicationOfADifferentScope_WhenHandlingHardDeleteApplication_ThenApplicationNotFoundIsReported` | AF-20a, Decision 4 |
| `GivenUnknownScope_WhenHandlingHardDeleteApplication_ThenApplicationNotFoundIsReported` | AF-20a, Decision 4 |
| `GivenAlreadyHardDeletedApplication_WhenHandlingHardDeleteApplication_ThenApplicationNotFoundIsReported` | AF-20a on repeat, Decision 6 |

Every refusal test also asserts the stored row is still present.

## Step 4 — Handler (green)

`HardDeleteApplicationCommandHandler` implementing the three-step flow from the design: load by
`PublicId` + `Scope.PublicId` in any deletion state → delete the record → return. Each step commented
with the UC/AF it implements, failures returned as errors on `DataOutput`.

- Verify: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"` green.

## Step 5 — Endpoint and DI

- `ApplicationController` — add `[HttpDelete("{id:guid}/hard")] HardDelete(Guid scopeId, Guid id)`
  with `[RoleRequirement((int)Roles.SystemAdmin)]`, building the command from the route values,
  dispatching through `CommandMediator`, and resolving through `ApplicationMessageMap.StatusCodes`.
  No `ApplyActor` call (Decision 2). XML doc naming UC-20, FR-AP-08, and AF-20a.
- `Startup.AddDependencies` — register
  `ICommandHandlerAsync<HardDeleteApplicationCommand, HardDeleteApplicationCommandOutput>`.

## Step 6 — Functional tests

`tests/Presentation/…WebApi.Tests/ApplicationControllerHardDeleteTests.cs`, reusing the seeding shape
of `ApplicationControllerDeleteTests`, authorised with `TestTokens.For(person.PublicId, role)`.

| Test | Covers |
| --- | --- |
| `GivenSystemAdmin_WhenHardDeleteApplication_ThenOkAndRowIsGone` | main flow + database state |
| `GivenLogicallyDeletedApplication_WhenHardDeleteApplication_ThenOkAndRowIsGone` | Decision 1 |
| `GivenApplicationRemoved_WhenHardDeleteApplication_ThenScopeAndOwnerSurvive` | Decision 5 |
| `GivenOwningScopeAdmin_WhenHardDeleteApplication_ThenForbidden` | Decision 3 |
| `GivenUserRole_WhenHardDeleteApplication_ThenForbidden` | precondition (attribute) |
| `GivenUnknownApplication_WhenHardDeleteApplication_ThenNotFound` | AF-20a |
| `GivenApplicationOfAnotherScope_WhenHardDeleteApplication_ThenNotFound` | AF-20a, Decision 4 |
| `GivenApplicationHardDeletedTwice_WhenHardDeleteApplication_ThenSecondCallIsNotFound` | AF-20a, Decision 6 |
| `GivenNoToken_WhenHardDeleteApplication_ThenUnauthorized` | precondition |

Refusal tests assert the persisted row is still present; success tests assert it is gone.

## Step 7 — Documentation

- `Testing Specification Document.md` §10: add `HardDeleteApplication` to the Command.Tests row, note
  `ApplicationControllerHardDeleteTests`, and update the suite totals line to UC-20.
- `README.md`: mark UC-20 ✅ in the use case tracker.

## Step 8 — Full suite

`dotnet test src/ArturRios.IdentityManager.sln` — both categories green before the pull request. The
pull request body references the use case and `Closes #21`.
