# UC-28 — Logical Delete Google User — Implementation Plan

Design: [2026-08-03-uc-28-logical-delete-google-user-design.md](../specs/2026-08-03-uc-28-logical-delete-google-user-design.md)
Issue: [#29](https://github.com/artur-rios/heimdall-api/issues/29) · Branch:
`feature/uc-28-logical-delete-google-user` · Milestone: Google Sign-In

Sequenced test-first per the Testing Specification §9: each implementation step is followed by the
tests that pin it, and the suite is run before the pull request.

---

## Step 1 — Branch and mark started

- `git switch main && git pull && git switch -c feature/uc-28-logical-delete-google-user`
- Move issue #29 to **In Progress**.

## Step 2 — Messages

Added to UC-27's `GoogleUserMessages` / `GoogleUserMessageMap`:

- `GoogleUserDeletedSuccessfully` → 200, carrying **both** the main flow and AF-28b.
- `NotAuthorizedToDeleteGoogleUser` → 403 (AF-28c).
- AF-28a reuses `GoogleUserNotFound` → 404.

## Step 3 — Command contract

- `Command/Input/DeleteGoogleUserCommand.cs` — `{ ScopeId, Id }` + `IActorScoped`, both from the route.
- `Command/Output/DeleteGoogleUserCommandOutput.cs` — `{ Id, AlreadyDeleted }`.

No validator: both fields are typed route parameters, so NFR-10 has nothing to reject that the route
would not have refused first.

## Step 4 — Handler

`Command/Handlers/DeleteGoogleUserCommandHandler.cs`, the shape of
`DeleteApplicationCommandHandler` with `IScopeOwnershipChecker` in place of its owner comparison.
The ordering matters and is commented as such:

1. Lookup **without** `!IsDeleted`, qualified by the route's scope → AF-28a.
2. `ActorMayManageScopeAsync` → AF-28c.
3. `IsDeleted` short-circuit → AF-28b, `UpdatedAt` untouched.
4. Flip and stamp → main flow.

## Step 5 — Unit tests (`Command.Tests/DeleteGoogleUserCommandHandlerTests.cs`)

| Test | Flow |
| --- | --- |
| `GivenActiveGoogleUser_…_ThenSetsIsDeletedAndStampsUpdatedAt` | main, FR-GO-15 |
| `GivenOwningScopeAdmin_…_ThenChecksOwnershipOfTheGoogleUsersScope` | step 2, asserts the scope asked about |
| `GivenNoSuchGoogleUser_…_ThenReturnsNotFoundError` | AF-28a |
| `GivenGoogleUserInAnotherScope_…_ThenReturnsNotFoundError` | AF-28a |
| `GivenAlreadyDeletedGoogleUser_…_ThenSucceedsIdempotentlyWithoutWriting` | AF-28b |
| `GivenNonOwningScopeAdmin_…_ThenReturnsNotAuthorizedError` | AF-28c |
| `GivenAlreadyDeletedGoogleUserAndUnauthorizedCaller_…_ThenReturnsNotAuthorizedError` | AF-28c before AF-28b |

## Step 6 — Presentation layer

- `GoogleUserController.Delete` — `[HttpDelete("{id:guid}")]`,
  `[RoleRequirement(SystemAdmin, ScopeAdmin)]`. The controller gains a `CommandMediator`.
- Register the handler in `Startup.AddDependencies`.

## Step 7 — Functional tests (`WebApi.Tests/GoogleUserControllerDeleteTests.cs`)

10 tests: the two authorized actors, AF-28a × 2, AF-28b, AF-28c × 3 (non-owning admin, the Google
User themselves, anonymous), plus two that reach past the endpoint — UC-27's read must stop
returning the record (FR-GO-17), and UC-25 must refuse to sign the account back in (AF-25d). A flag
nothing honours is not a deletion.

## Step 8 — Run the suite

`dotnet test src/ArturRios.Heimdall.sln` — both categories green before the pull request.

## Step 9 — Tracker and pull request

- Mark UC-28 done in the README backlog; record the new test classes and refreshed totals.
- Open the PR into `main` with `Closes #29`.
