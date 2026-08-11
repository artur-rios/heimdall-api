# UC-27 — View Google User — Implementation Plan

Design: [2026-08-03-uc-27-view-google-user-design.md](../specs/2026-08-03-uc-27-view-google-user-design.md)
Issue: [#28](https://github.com/artur-rios/heimdall-api/issues/28) · Branch:
`feature/uc-27-view-google-user` · Milestone: Google Sign-In

Sequenced test-first per the Testing Specification §9: each implementation step is followed by the
tests that pin it, and the suite is run before the pull request.

---

## Step 1 — Branch and mark started

- `git switch main && git pull && git switch -c feature/uc-27-view-google-user`
- Move issue #28 to **In Progress**.

## Step 2 — Messages

- New `Shared/Messages/GoogleUserMessages.cs` and `GoogleUserMessageMap.cs` — Google Users are their
  own entity with their own endpoints, and UC-28/UC-29 land in the same files next.
- Six messages: two successes (200), `GoogleUserNotFound` and `ScopeNotFound` (404),
  `NotAuthorizedToViewGoogleUser` and `NotScopeOwner` (403).

## Step 3 — Query contracts (`…Query`)

- `Output/GoogleUserOutput.cs` — FR-GO-05's registered fields plus timestamps, internal `Id` omitted
  (NFR-15). `GoogleId` included; the XML doc records why it is not withheld.
- `Input/GetGoogleUserByIdQuery.cs` — `{ ScopeId, Id, IncludeDeleted }` + `IActorScoped`.
- `Input/ListScopeGoogleUsersQuery.cs` — `{ ScopeId, Name?, Email?, IncludeDeleted }` + `IActorScoped`.

## Step 4 — Handlers

- `Handlers/GetGoogleUserByIdQueryHandler.cs` — projection qualified by the route's scope, then
  `self || ActorMayManageScopeAsync`. Private projection type carries the internal scope id the rule
  needs without letting it reach the payload, as `GetPersonByIdQueryHandler` does.
- `Handlers/ListScopeGoogleUsersQueryHandler.cs` — the shape of `ListScopePersonsQueryHandler`:
  scope check, ownership check, then filter and paginate.

## Step 5 — Unit tests (`Query.Tests`)

`AsyncFakeRepository<T>` for repositories, Moq for `IScopeOwnershipChecker` (its own rule is already
covered in `Shared.Tests`), Bogus where the descriptive fields do not matter.

`GetGoogleUserByIdQueryHandlerTests`:

| Test | Flow |
| --- | --- |
| `GivenSystemAdmin_…_ThenReturnsIt` | main, FR-GO-05 projection |
| `GivenOwningScopeAdmin_…_ThenReturnsIt` | main; asserts the checker is asked about the right scope |
| `GivenGoogleUserReadingThemselves_…_ThenReturnsItWithoutConsultingOwnership` | main, self-read |
| `GivenNoSuchGoogleUser_…_ThenReturnsNotFoundError` | AF-27a |
| `GivenGoogleUserInAnotherScope_…_ThenReturnsNotFoundError` | AF-27a |
| `GivenLogicallyDeletedGoogleUser_…_ThenReturnsNotFoundError` | AF-27a, FR-GO-17 |
| `GivenLogicallyDeletedGoogleUserAndIncludeDeleted_…_ThenReturnsIt` | FR-GO-17 escape hatch |
| `GivenNonOwningScopeAdmin_…_ThenReturnsNotAuthorizedError` | AF-27b |
| `GivenAnotherUser_…_ThenReturnsNotAuthorizedError` | AF-27b |

`ListScopeGoogleUsersQueryHandlerTests`: main flow (scope isolation + FR-GO-17), include-deleted,
name filter, email filter, AF-27a × 2 (unknown and deleted scope), AF-27b, and an empty page.

## Step 6 — Presentation layer

- New `GoogleUserController`, `[Route("api/scopes/{scopeId:guid}/google-users")]`.
- `List` carries `[RoleRequirement(SystemAdmin, ScopeAdmin)]`; `GetById` carries **none** — a Google
  User's token is `User`-role (FR-GO-04), so any attribute would lock out an actor UC-27 grants.
- Register both handlers in `Startup.AddDependencies` beside the application query handlers.

## Step 7 — Functional tests (`WebApi.Tests/GoogleUserControllerViewTests.cs`)

One class for both endpoints, because the point being pinned is the asymmetry between them.
17 tests: 10 by-id, 7 listing, including the Google User who may read themselves but may not list.

## Step 8 — Run the suite

`dotnet test src/ArturRios.Heimdall.sln` — both categories green before the pull request.

## Step 9 — Tracker and pull request

- Mark UC-27 done in the README backlog; record the new test classes and refreshed totals in the
  Testing Specification inventory.
- Open the PR into `main` with `Closes #28`.
