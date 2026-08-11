# UC-23: Promote User to Scope Owner — Implementation Plan

Design: [2026-08-03-uc-23-promote-user-to-scope-owner-design.md](../specs/2026-08-03-uc-23-promote-user-to-scope-owner-design.md)
Issue: [#24](https://github.com/artur-rios/heimdall-api/issues/24)
Branch: `feature/uc-23-promote-user-to-scope-owner`

Sequenced test-first per the
[Testing Specification Document](../../requirements/Testing%20Specification%20Document.md) §9.

---

## Step 1 — Messages and status map

- `PersonMessages` — add `ScopeUserPromotedSuccessfully`
  (`"Person promoted to scope owner successfully."`), `PersonNotScopeUser`
  (`"The person must be an existing, non-deleted User of this scope."`), and `AlreadyScopeAdmin`
  (`"Person already holds the ScopeAdmin role."`), each with a doc comment naming UC-23 and its flow.
- `PersonMessageMap` — `ScopeUserPromotedSuccessfully` → 200, `PersonNotScopeUser` → 400,
  `AlreadyScopeAdmin` → 409. `ScopeNotFound` (404), `NotScopeOwner` (403) and `EmailAlreadyExists`
  (409) are already mapped and are reused for AF-23a, AF-23c and open question A.

## Step 2 — Command and output

- `PromoteScopeUserCommand : BaseCommand, IActorScoped` — `ScopeId`, `PersonId` (both from the
  route), `ActingPersonId`, `ActingRole`. No validator (no body to guard).
- `PromoteScopeUserCommandOutput : CommandOutput` — `Id`, `Name`, `Email`, `Role`, `EmailVerified`,
  `OwnedScopeIds`, `CreatedAt`, `UpdatedAt` (Decisions 9 and 10).

## Step 3 — Handler tests (red)

`tests/Application/…Command.Tests/PromoteScopeUserCommandHandlerTests.cs`, reusing the seeding shape
of `AddScopeOwnerCommandHandlerTests` (scope + person with optional membership/ownership) and mocking
`IScopeOwnershipChecker` with Moq.

| Test | Covers |
| --- | --- |
| `GivenSystemAdminAndScopeUser_WhenHandlingPromoteScopeUser_ThenPersonBecomesScopeOwner` | main flow |
| `GivenExistingOwnerActor_WhenHandlingPromoteScopeUser_ThenPersonBecomesScopeOwner` | main flow, FR-SC-13 |
| `GivenScopeUser_WhenHandlingPromoteScopeUser_ThenMembershipIsRemovedAndUpdatedAtIsStamped` | UC-23 steps 4–5, Decisions 7–8 |
| `GivenOutput_WhenHandlingPromoteScopeUser_ThenItCarriesPublicIdentifiersOnly` | SRD §4.0, Decision 9 |
| `GivenUnknownScope_WhenHandlingPromoteScopeUser_ThenScopeNotFoundIsReported` | AF-23a |
| `GivenLogicallyDeletedScope_WhenHandlingPromoteScopeUser_ThenScopeNotFoundIsReported` | AF-23a, Decision 5 |
| `GivenUnknownPerson_WhenHandlingPromoteScopeUser_ThenPersonNotScopeUserIsReported` | AF-23b |
| `GivenLogicallyDeletedUser_WhenHandlingPromoteScopeUser_ThenPersonNotScopeUserIsReported` | AF-23b, Decision 6 |
| `GivenUserOfAnotherScope_WhenHandlingPromoteScopeUser_ThenPersonNotScopeUserIsReported` | AF-23b |
| `GivenSystemAdminPerson_WhenHandlingPromoteScopeUser_ThenPersonNotScopeUserIsReported` | AF-23b |
| `GivenScopeAdminNotOwningTheScope_WhenHandlingPromoteScopeUser_ThenNotScopeOwnerIsReported` | AF-23c |
| `GivenPersonAlreadyScopeAdmin_WhenHandlingPromoteScopeUser_ThenAlreadyScopeAdminIsReported` | AF-23d, Decision 3 |
| `GivenUnauthorizedActorAndUnknownPerson_WhenHandlingPromoteScopeUser_ThenNotScopeOwnerIsReported` | Decision 2 (ordering) |
| `GivenEmailAlreadyUsedByAnAdmin_WhenHandlingPromoteScopeUser_ThenEmailAlreadyExistsIsReported` | open question A — **only if approved** |
| `GivenEmailUsedOnlyByAUserOfAnotherScope_WhenHandlingPromoteScopeUser_ThenPersonBecomesScopeOwner` | open question A — the namespaces are independent |

Every refusal test also asserts the person's `RoleId`, `ScopeMembership` and `ScopeOwnerships` are
unchanged.

## Step 4 — Handler (green)

`PromoteScopeUserCommandHandler` implementing the flow from the design: scope lookup → ownership
check → person lookup → AF-23d → AF-23b → (open question A) → role change + membership removal +
ownership insert through `personWriter.UpdateAsync` → return the person. Each step commented with the
UC/AF it implements; failures returned as errors on `DataOutput`.

- Verify: `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"` green.

## Step 5 — Endpoint and DI

- `PersonController` — add
  `[HttpPost("scopes/{scopeId:guid}/users/{personId:guid}/promote")] PromoteScopeUser(Guid scopeId, Guid personId)`
  with `[RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]`, building the command from
  the route values, calling `HttpContext.ApplyActor(command)`, dispatching through `CommandMediator`,
  and resolving through `PersonMessageMap.StatusCodes`. XML doc naming UC-23,
  FR-SC-08/FR-SC-13/FR-RO-03, and which flows the attribute versus the handler settles.
- `Startup.AddDependencies` — register
  `ICommandHandlerAsync<PromoteScopeUserCommand, PromoteScopeUserCommandOutput>`.

## Step 6 — Functional tests

`tests/Presentation/…WebApi.Tests/PersonControllerPromoteScopeUserTests.cs`, reusing the seeding shape
of `PersonControllerAddScopeOwnerTests`, authorised with `TestTokens.For(person.PublicId, role)`. The
request carries no body, so the gateway posts `new { }`.

| Test | Covers |
| --- | --- |
| `GivenSystemAdmin_WhenPostPromote_ThenOkAndRowsAreMoved` | main flow + database state |
| `GivenExistingOwner_WhenPostPromote_ThenOkAndRowsAreMoved` | main flow, FR-SC-13 |
| `GivenScopeAdminOfAnotherScope_WhenPostPromote_ThenForbidden` | AF-23c |
| `GivenUserRole_WhenPostPromote_ThenForbidden` | precondition (attribute) |
| `GivenUnknownScope_WhenPostPromote_ThenNotFound` | AF-23a |
| `GivenLogicallyDeletedScope_WhenPostPromote_ThenNotFound` | AF-23a, Decision 5 |
| `GivenUnknownPerson_WhenPostPromote_ThenBadRequest` | AF-23b |
| `GivenUserOfAnotherScope_WhenPostPromote_ThenBadRequest` | AF-23b |
| `GivenScopeAdminPerson_WhenPostPromote_ThenConflict` | AF-23d |
| `GivenPersonPromotedTwice_WhenPostPromote_ThenSecondCallIsConflict` | AF-23d, no second row |
| `GivenNoToken_WhenPostPromote_ThenUnauthorized` | precondition |

Refusal tests assert the `scope_user` row survives and no `scope_owner` row was created; the success
tests assert the inverse.

## Step 7 — Documentation

- `Testing Specification Document.md` §10: add `PromoteScopeUser` to the Command.Tests row, note
  `PersonControllerPromoteScopeUserTests`, and update the suite totals line to UC-23.
- `README.md`: mark UC-23 ✅ in the use case tracker.

## Step 8 — Full suite

`dotnet test src/ArturRios.Heimdall.sln` — both categories green before the pull request. The
pull request body references the use case and `Closes #24`.
