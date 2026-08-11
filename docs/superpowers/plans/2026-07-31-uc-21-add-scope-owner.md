# UC-21: Add Scope Owner — Implementation Plan

Design: [2026-07-31-uc-21-add-scope-owner-design.md](../specs/2026-07-31-uc-21-add-scope-owner-design.md)
Issue: [#22](https://github.com/artur-rios/heimdall-api/issues/22)
Branch: `feature/uc-21-add-scope-owner`

Sequenced test-first per the
[Testing Specification Document](../../requirements/Testing%20Specification%20Document.md) §9.

---

## Step 1 — Messages and status map

- `PersonMessages` — add `ScopeOwnerAddedSuccessfully` (`"Scope owner added successfully."`),
  `AlreadyScopeOwner` (`"Person is already an owner of this scope."`), and
  `PersonNotValidScopeAdmin` (`"The person must be an existing, non-deleted ScopeAdmin."`), each with
  a doc comment naming UC-21 and its flow.
- `PersonMessageMap` — `ScopeOwnerAddedSuccessfully` → 201, `AlreadyScopeOwner` → 200,
  `PersonNotValidScopeAdmin` → 400. `ScopeNotFound` (404) and `NotScopeOwner` (403) are already
  mapped and are reused for AF-21a and AF-21c.

## Step 2 — Command and output

- `AddScopeOwnerCommand : BaseCommand, IActorScoped` — `ScopeId`, `PersonId` (both from the route),
  `ActingPersonId`, `ActingRole`. No validator (Decision 4).
- `AddScopeOwnerCommandOutput : CommandOutput` — `ScopeId`, `PersonId`, `AlreadyOwner`
  (Decisions 6 and 11).

## Step 3 — Handler tests (red)

`tests/Application/…Command.Tests/AddScopeOwnerCommandHandlerTests.cs`, reusing the seeding shape of
`CreateScopeOwnerCommandHandlerTests` (scope + `ScopeAdmin` with optional ownership) and mocking
`IScopeOwnershipChecker` with Moq.

| Test | Covers |
| --- | --- |
| `GivenSystemAdminAndScopeAdminPerson_WhenHandlingAddScopeOwner_ThenOwnershipIsAdded` | main flow |
| `GivenExistingOwnerActor_WhenHandlingAddScopeOwner_ThenOwnershipIsAdded` | main flow, FR-SC-09 |
| `GivenPersonOwningAnotherScope_WhenHandlingAddScopeOwner_ThenExistingOwnershipsSurvive` | main flow scoping |
| `GivenOutput_WhenHandlingAddScopeOwner_ThenItCarriesPublicIdentifiersOnly` | SRD §4.0, Decision 11 |
| `GivenUnknownScope_WhenHandlingAddScopeOwner_ThenScopeNotFoundIsReported` | AF-21a |
| `GivenLogicallyDeletedScope_WhenHandlingAddScopeOwner_ThenScopeNotFoundIsReported` | AF-21a, Decision 8 |
| `GivenUnknownPerson_WhenHandlingAddScopeOwner_ThenPersonNotValidScopeAdminIsReported` | AF-21b |
| `GivenLogicallyDeletedPerson_WhenHandlingAddScopeOwner_ThenPersonNotValidScopeAdminIsReported` | AF-21b, Decision 10 |
| `GivenPersonWithUserRole_WhenHandlingAddScopeOwner_ThenPersonNotValidScopeAdminIsReported` | AF-21b |
| `GivenPersonWithSystemAdminRole_WhenHandlingAddScopeOwner_ThenPersonNotValidScopeAdminIsReported` | AF-21b |
| `GivenScopeAdminNotOwningTheScope_WhenHandlingAddScopeOwner_ThenNotScopeOwnerIsReported` | AF-21c |
| `GivenPersonAlreadyOwner_WhenHandlingAddScopeOwner_ThenAlreadyOwnerIsReportedAndNoRowIsAdded` | AF-21d, Decision 6 |
| `GivenUnauthorizedActorAndUnknownPerson_WhenHandlingAddScopeOwner_ThenNotScopeOwnerIsReported` | Decision 2 (ordering) |

Every refusal test also asserts the person's `ScopeOwnerships` count is unchanged.

## Step 4 — Handler (green)

`AddScopeOwnerCommandHandler` implementing the six-step flow from the design: scope lookup →
ownership check → person lookup → idempotent short-circuit → add the join row through
`personWriter.UpdateAsync` → return. Each step commented with the UC/AF it implements; failures
returned as errors on `DataOutput`.

- Verify: `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"` green.

## Step 5 — Endpoint and DI

- `PersonController` — add
  `[HttpPost("scopes/{scopeId:guid}/owners/{personId:guid}")] AddScopeOwner(Guid scopeId, Guid personId)`
  with `[RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]`, building the command from
  the route values, calling `HttpContext.ApplyActor(command)`, dispatching through `CommandMediator`,
  and resolving through `PersonMessageMap.StatusCodes`. XML doc naming UC-21, FR-SC-08/09, and which
  flows the attribute versus the handler settles.
- `Startup.AddDependencies` — register
  `ICommandHandlerAsync<AddScopeOwnerCommand, AddScopeOwnerCommandOutput>`.

## Step 6 — Functional tests

`tests/Presentation/…WebApi.Tests/PersonControllerAddScopeOwnerTests.cs`, reusing the seeding shape of
`PersonControllerCreateScopeOwnerTests`, authorised with `TestTokens.For(person.PublicId, role)`. The
request carries no body, so the gateway posts `new { }` as `AuthControllerResendVerificationTests`
does.

| Test | Covers |
| --- | --- |
| `GivenSystemAdmin_WhenPostScopeOwner_ThenCreatedAndRowExists` | main flow + database state |
| `GivenExistingOwner_WhenPostScopeOwner_ThenCreatedAndRowExists` | main flow, FR-SC-09 |
| `GivenScopeAdminOfAnotherScope_WhenPostScopeOwner_ThenForbidden` | AF-21c |
| `GivenUserRole_WhenPostScopeOwner_ThenForbidden` | precondition (attribute) |
| `GivenUnknownScope_WhenPostScopeOwner_ThenNotFound` | AF-21a |
| `GivenLogicallyDeletedScope_WhenPostScopeOwner_ThenNotFound` | AF-21a, Decision 8 |
| `GivenUnknownPerson_WhenPostScopeOwner_ThenBadRequest` | AF-21b |
| `GivenLogicallyDeletedPerson_WhenPostScopeOwner_ThenBadRequest` | AF-21b, Decision 10 |
| `GivenUserPerson_WhenPostScopeOwner_ThenBadRequest` | AF-21b |
| `GivenPersonAddedTwice_WhenPostScopeOwner_ThenSecondCallIsOkAndRowIsNotDuplicated` | AF-21d, Decision 6 |
| `GivenNoToken_WhenPostScopeOwner_ThenUnauthorized` | precondition |

Refusal tests assert no `scope_owner` row was created; success tests assert exactly one exists.

## Step 7 — Documentation

- `Testing Specification Document.md` §10: add `AddScopeOwner` to the Command.Tests row, note
  `PersonControllerAddScopeOwnerTests`, and update the suite totals line to UC-21.
- `README.md`: mark UC-21 ✅ in the use case tracker.

## Step 8 — Full suite

`dotnet test src/ArturRios.Heimdall.sln` — both categories green before the pull request. The
pull request body references the use case and `Closes #22`.
