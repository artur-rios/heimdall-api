# UC-29 — Hard Delete Google User — Implementation Plan

Design: [2026-08-03-uc-29-hard-delete-google-user-design.md](../specs/2026-08-03-uc-29-hard-delete-google-user-design.md)
Issue: [#30](https://github.com/artur-rios/heimdall-api/issues/30) · Branch:
`feature/uc-29-hard-delete-google-user` · Milestone: Google Sign-In

Sequenced test-first per the Testing Specification §9: each implementation step is followed by the
tests that pin it, and the suite is run before the pull request.

---

## Step 1 — Branch and mark started

- `git switch main && git pull && git switch -c feature/uc-29-hard-delete-google-user`
- Move issue #30 to **In Progress**.

## Step 2 — Message

One addition to UC-27's files: `GoogleUserHardDeletedSuccessfully` → 200. AF-29a reuses
`GoogleUserNotFound` → 404.

## Step 3 — Command contract

- `Command/Input/HardDeleteGoogleUserCommand.cs` — `{ ScopeId, Id }`, both from the route, and
  **no** `IActorScoped`: UC-29's only actor is the System Admin, so the endpoint's attribute settles
  authorization entirely and the handler has no data-dependent rule left. UC-20 does the same.
- `Command/Output/HardDeleteGoogleUserCommandOutput.cs` — `{ Id }` alone; there are no dependents to
  count.

No validator: both fields are typed route parameters.

## Step 4 — Handler

`Command/Handlers/HardDeleteGoogleUserCommandHandler.cs`, the shape of
`HardDeleteApplicationCommandHandler`:

1. Lookup qualified by the route's scope, **without** an `!IsDeleted` filter — a soft-deleted Google
   User is what a cleanup pass starts from → AF-29a collapses four situations into one 404.
2. `DeleteAsync`, with no dependent removed first (there are none) → main flow.

The XML doc records why there is no self-deletion refusal here, unlike UC-09 and UC-10: a Google
User can never hold `SystemAdmin` (FR-GO-04), so the only permitted caller can never be the target.

## Step 5 — Unit tests (`Command.Tests/HardDeleteGoogleUserCommandHandlerTests.cs`)

| Test | Flow |
| --- | --- |
| `GivenGoogleUser_…_ThenRemovesTheRecord` | main, FR-GO-16 |
| `GivenLogicallyDeletedGoogleUser_…_ThenRemovesTheRecord` | the cleanup-pass case |
| `GivenNoSuchGoogleUser_…_ThenReturnsNotFoundError` | AF-29a |
| `GivenGoogleUserInAnotherScope_…_ThenReturnsNotFoundError` | AF-29a |
| `GivenAlreadyHardDeletedGoogleUser_…_ThenReturnsNotFoundError` | AF-29a on a repeat |

No authorization test at this level — there is no rule here to test.

## Step 6 — Presentation layer

- `GoogleUserController.HardDelete` — `[HttpDelete("{id:guid}/hard")]`,
  `[RoleRequirement(SystemAdmin)]`.
- Register the handler in `Startup.AddDependencies`.

## Step 7 — Functional tests (`WebApi.Tests/GoogleUserControllerHardDeleteTests.cs`)

9 tests: main flow, the soft-deleted record, AF-29a × 3, and the authorization the handler delegates
entirely to the endpoint — including an **owning** Scope Admin, which is the whole difference
between UC-28 and UC-29. One test asserts the scope and its other Google Users survive: the foreign
key points from the Google User to the scope, and a cascade the other way would be catastrophic and
silent.

## Step 8 — Run the suite

`dotnet test src/ArturRios.Heimdall.sln` — both categories green before the pull request.

## Step 9 — Tracker and pull request

- Mark UC-29 done in the README backlog; record the new test classes and refreshed totals.
- Open the PR into `main` with `Closes #30`.
