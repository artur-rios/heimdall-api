# UC-22: Remove Scope Owner — Implementation Plan

Design: [2026-07-31-uc-22-remove-scope-owner-design.md](../specs/2026-07-31-uc-22-remove-scope-owner-design.md)
Issue: [#23](https://github.com/artur-rios/identity-manager-api/issues/23)
Branch: `feature/uc-22-remove-scope-owner`

Sequenced test-first per the
[Testing Specification Document](../../requirements/Testing%20Specification%20Document.md) §9.

---

## Step 1 — Messages and status map

- `PersonMessages` — add `ScopeOwnerRemovedSuccessfully` (`"Scope owner removed successfully."`) and
  `PersonNotScopeOwner` (`"The person is not an owner of this scope."`), each with a doc comment
  naming UC-22 and its flow, and noting how `PersonNotScopeOwner` differs from `NotScopeOwner`.
- `PersonMessageMap` — `ScopeOwnerRemovedSuccessfully` → 200, `PersonNotScopeOwner` → 404.
  `ScopeNotFound` (404), `NotScopeOwner` (403) and `ScopeWouldLoseLastOwner` (409) are already mapped
  and are reused for AF-22a, AF-22c and AF-22b.

## Step 2 — Command and output

- `RemoveScopeOwnerCommand : BaseCommand, IActorScoped` — `ScopeId`, `PersonId` (both from the
  route), `ActingPersonId`, `ActingRole`. No validator (design Shape §).
- `RemoveScopeOwnerCommandOutput : CommandOutput` — `ScopeId`, `PersonId` (Decisions 8 and 13; no
  `AlreadyRemoved` flag).

## Step 3 — Handler tests (red)

`tests/Application/…Command.Tests/RemoveScopeOwnerCommandHandlerTests.cs`, reusing the seeding shape
of `AddScopeOwnerCommandHandlerTests` (scope + `ScopeAdmin` with optional ownership) and mocking
`IScopeOwnershipChecker` with Moq.

| Test | Covers |
| --- | --- |
| `GivenSystemAdminAndCoOwnedScope_WhenHandlingRemoveScopeOwner_ThenOwnershipIsRemoved` | main flow |
| `GivenCoOwnerActor_WhenHandlingRemoveScopeOwner_ThenOwnershipIsRemoved` | main flow, FR-SC-10 |
| `GivenPersonOwningAnotherScope_WhenHandlingRemoveScopeOwner_ThenOtherOwnershipsSurvive` | main flow scoping |
| `GivenOutput_WhenHandlingRemoveScopeOwner_ThenItCarriesPublicIdentifiersOnly` | SRD §4.0, Decision 13 |
| `GivenUnknownScope_WhenHandlingRemoveScopeOwner_ThenScopeNotFoundIsReported` | AF-22a |
| `GivenLogicallyDeletedScope_WhenHandlingRemoveScopeOwner_ThenScopeNotFoundIsReported` | AF-22a, Decision 3 |
| `GivenUnknownPerson_WhenHandlingRemoveScopeOwner_ThenPersonNotScopeOwnerIsReported` | AF-22a, Decision 4 |
| `GivenPersonOwningOnlyAnotherScope_WhenHandlingRemoveScopeOwner_ThenPersonNotScopeOwnerIsReported` | AF-22a |
| `GivenSoleOwner_WhenHandlingRemoveScopeOwner_ThenScopeWouldLoseLastOwnerIsReported` | AF-22b, Decision 7 |
| `GivenOnlyCoOwnerIsLogicallyDeleted_WhenHandlingRemoveScopeOwner_ThenScopeWouldLoseLastOwnerIsReported` | AF-22b, Decision 6 |
| `GivenLogicallyDeletedTargetWithLiveCoOwner_WhenHandlingRemoveScopeOwner_ThenStaleOwnershipIsRemoved` | Decisions 5 and 6 |
| `GivenActorRemovingThemselvesWithCoOwnerRemaining_WhenHandlingRemoveScopeOwner_ThenOwnershipIsRemoved` | Decision 12 |
| `GivenScopeAdminNotOwningTheScope_WhenHandlingRemoveScopeOwner_ThenNotScopeOwnerIsReported` | AF-22c |
| `GivenUnauthorizedActorAndUnknownPerson_WhenHandlingRemoveScopeOwner_ThenNotScopeOwnerIsReported` | Decision 2 (ordering) |

Every refusal test also asserts the target's ownership row survives.

## Step 4 — Handler (green)

`RemoveScopeOwnerCommandHandler` implementing the six-step flow from the design: scope lookup →
ownership check → person + ownership-row lookup → last-owner guard → remove the join row through
`personWriter.UpdateAsync` → return. Each step commented with the UC/AF it implements; failures
returned as errors on `DataOutput`.

- Verify: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"` green.

## Step 5 — Endpoint and DI

- `PersonController` — add
  `[HttpDelete("scopes/{scopeId:guid}/owners/{personId:guid}")] RemoveScopeOwner(Guid scopeId, Guid personId)`
  with `[RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]`, building the command from
  the route values, calling `HttpContext.ApplyActor(command)`, dispatching through `CommandMediator`,
  and resolving through `PersonMessageMap.StatusCodes`. XML doc naming UC-22, FR-SC-08/FR-SC-10, and
  which flows the attribute versus the handler settles.
- `Startup.AddDependencies` — register
  `ICommandHandlerAsync<RemoveScopeOwnerCommand, RemoveScopeOwnerCommandOutput>`.

## Step 6 — Functional tests

`tests/Presentation/…WebApi.Tests/PersonControllerRemoveScopeOwnerTests.cs`, reusing the seeding shape
of `PersonControllerAddScopeOwnerTests`, authorised with `TestTokens.For(person.PublicId, role)` and
issuing `Gateway.DeleteAsync<…>(route)` as `ApplicationControllerDeleteTests` does.

| Test | Covers |
| --- | --- |
| `GivenSystemAdmin_WhenDeleteScopeOwner_ThenOkAndRowIsGone` | main flow + database state |
| `GivenCoOwner_WhenDeleteScopeOwner_ThenOkAndRowIsGone` | main flow, FR-SC-10 |
| `GivenScopeAdminOfAnotherScope_WhenDeleteScopeOwner_ThenForbidden` | AF-22c |
| `GivenUserRole_WhenDeleteScopeOwner_ThenForbidden` | precondition (attribute) |
| `GivenUnknownScope_WhenDeleteScopeOwner_ThenNotFound` | AF-22a |
| `GivenLogicallyDeletedScope_WhenDeleteScopeOwner_ThenNotFound` | AF-22a, Decision 3 |
| `GivenUnknownPerson_WhenDeleteScopeOwner_ThenNotFound` | AF-22a |
| `GivenPersonNotOwningTheScope_WhenDeleteScopeOwner_ThenNotFound` | AF-22a, Decision 4 |
| `GivenSoleOwner_WhenDeleteScopeOwner_ThenConflictAndRowSurvives` | AF-22b |
| `GivenOwnerRemovedTwice_WhenDeleteScopeOwner_ThenSecondCallIsNotFound` | Decision 8 |
| `GivenNoToken_WhenDeleteScopeOwner_ThenUnauthorized` | precondition |

Refusal tests assert the `scope_owner` row is still present; success tests assert it is gone and the
co-owner's row is untouched.

## Step 7 — Documentation

- `Testing Specification Document.md` §10: add `RemoveScopeOwner` to the Command.Tests row, note
  `PersonControllerRemoveScopeOwnerTests`, and update the suite totals line to UC-22.
- `README.md`: mark UC-22 ✅ in the use case tracker.

## Step 8 — Full suite

`dotnet test src/ArturRios.IdentityManager.sln` — both categories green before the pull request. The
pull request body references the use case and `Closes #23`.
