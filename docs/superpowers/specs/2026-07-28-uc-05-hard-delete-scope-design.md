# UC-05: Hard Delete Scope — Design

## Summary

Implement UC-05 (Hard Delete Scope, FR-SC-06): let a **System Admin** permanently remove a scope via
`DELETE /api/scopes/{id}/hard`. Deleting the scope also permanently removes every `User` person
belonging to it (via `SCOPE_USER`), every Google User in the scope, every application in the scope,
and all `SCOPE_OWNER` / `SCOPE_USER` join rows referencing it. Scope Admins who own the scope are
**not** removed — they may own other scopes (their `SCOPE_OWNER` rows go, the person records stay).

This is a write flow that mirrors the existing UC-04 (Logical Delete) pattern end-to-end
(command → handler → output → controller → messages/status map → DI), plus unit and functional tests.
It coexists with UC-04's logical delete on `DELETE /api/scopes/{id}`; the `/hard` suffix disambiguates
(System Requirements §5.1).

No schema change is required: the persistence already declares `ON DELETE CASCADE` on the relevant
foreign keys, so **no EF migration** is needed.

## Scope of the change

- **Actor / auth:** System Admin only (`[RoleRequirement((int)Roles.SystemAdmin)]`).
- **Cascade (spec §UC-05 postcondition, §8 deletion strategy, NFR-08 / NFR-14):** hard delete of the
  scope permanently removes its `SCOPE_USER` persons, its Google Users, its applications, and its
  `SCOPE_OWNER` / `SCOPE_USER` join rows. Owner **person** records (`ScopeAdmin`s) are left untouched.
  The Use Case Specification Document's UC-05 section is updated to include Google Users so it agrees
  with §8 / NFR-14 (the same gap UC-04 had).
- **No validator:** the only input is the route GUID; there are no body fields to validate. YAGNI.
- **Output (decided):** `HardDeleteScopeCommandOutput` returns the scope's `PublicId` plus
  `DeletedUserCount`, `DeletedGoogleUserCount`, and `DeletedApplicationCount`, mirroring UC-04 so the
  two delete endpoints stay symmetric.

## Cascade topology (why the handler deletes what it deletes)

The persistence layer (`EntityMaps/*DbMap.cs`, confirmed in the `InitialCreate` migration) declares:

| Foreign key | On delete | Effect when the **scope** row is removed |
| --- | --- | --- |
| `scope_owner.scope_id → scope` | Cascade | `SCOPE_OWNER` rows removed automatically |
| `scope_user.scope_id → scope` | Cascade | `SCOPE_USER` rows removed automatically |
| `application.scope_id → scope` | Cascade | applications removed automatically |
| `google_user.scope_id → scope` | Cascade | Google Users removed automatically |
| `scope_user.person_id → person` | Cascade | (removing a **person** removes their `SCOPE_USER` row) |
| `application.owner_id → person` | Cascade | (removing a **person** removes the apps they own) |

The one gap: **`User` person records have no cascade from the scope** — the FK runs Person → ScopeUser,
not the other way. So the User persons must be deleted **explicitly** by the handler; deleting them
cascades away their `SCOPE_USER` rows.

Applications and Google Users *would* be removed by the scope's DB cascade alone, but the handler
deletes them **explicitly** as well, before deleting the scope. This keeps the handler fully
unit-testable with `AsyncFakeRepository` (which does not simulate DB cascade) and matches UC-04's
explicit-cascade style. The redundant DB cascade on the already-removed rows is a harmless no-op.

## Count semantics (decided)

`DeletedUserCount` / `DeletedGoogleUserCount` / `DeletedApplicationCount` report the **total** number
of `SCOPE_USER` persons, Google Users, and applications **belonging to the scope**, counted regardless
of their individual `IsDeleted` state (hard delete removes them all either way). This matches UC-04's
count semantics.

## Main flow (spec §UC-05)

1. System Admin sends `DELETE /api/scopes/{id}/hard`.
2. Load the scope by `PublicId`. The lookup does **not** filter `IsDeleted`, so an already
   logically-deleted scope can still be hard-deleted. Not found → AF-05a.
3. Query the scope's members (totals, ignoring their `IsDeleted`): its `SCOPE_USER` persons, its
   Google Users, its applications. Their counts become the output totals.
4. Permanently delete the applications (`DeleteRangeAsync`), then the Google Users
   (`DeleteRangeAsync`), then the User persons (`DeleteRangeAsync` — cascades away their `SCOPE_USER`
   rows). Each call is skipped when its set is empty.
5. Permanently delete the scope (`DeleteAsync`) — the DB cascade removes the `SCOPE_OWNER` rows and
   any remaining `SCOPE_USER` rows.
6. Return `200 OK` with `{ id, deletedUserCount, deletedGoogleUserCount, deletedApplicationCount }`
   and `ScopeMessages.ScopeHardDeletedSuccessfully`.

Deletion order (applications → Google Users → persons → scope) never violates a foreign key: the only
inter-member FK is `application.owner_id → person`, and applications are removed first.

## Alternative flows

| ID | Condition | Outcome | Implementation |
| --- | --- | --- | --- |
| AF-05a | Scope not found | 404 Not Found | Handler: lookup returns null → `ScopeMessages.ScopeNotFound` (already mapped to 404) |
| (auth) | Actor not System Admin | 403 Forbidden | `[RoleRequirement]` on the controller action (functional test only) |
| (auth) | Unauthenticated | 401 Unauthorized | `AuthenticationMiddleware` (functional test only) |

Unlike UC-04, there is **no idempotent already-deleted flow** — a hard delete either finds the scope
and removes it, or returns 404.

## Cascade / member queries

Persons have no `ScopeId`; membership is the `ScopeUser` join, reciprocated by
`Person.ScopeMembership` (a one-to-one nav). SCOPE_USER persons of the scope are found via:

```
personReader.Query().Where(p => p.ScopeMembership != null && p.ScopeMembership.ScopeId == scope.Id)
```

Google Users and applications each have a direct `ScopeId`, so they are found via
`googleUserReader.Query().Where(g => g.ScopeId == scope.Id)` and
`applicationReader.Query().Where(a => a.ScopeId == scope.Id)`. All filters use the scope's internal
`Id` (never `PublicId`). The internal `Id`s of the found entities feed `DeleteRangeAsync(ids)`.

## Components (new unless noted)

| Layer | File | Notes |
| --- | --- | --- |
| Command / Input | `Command/Input/HardDeleteScopeCommand.cs` | `: BaseCommand` — `Id` (Guid, from route) |
| Command / Handler | `Command/Handlers/HardDeleteScopeCommandHandler.cs` | deps: read-only + writable repositories for `Scope`, `Person`, `GoogleUser`, `Application` |
| Command / Output | `Command/Output/HardDeleteScopeCommandOutput.cs` | `: CommandOutput` — `Id`, `DeletedUserCount`, `DeletedGoogleUserCount`, `DeletedApplicationCount` |
| Shared / Messages | `Shared/Messages/ScopeMessages.cs` (edit) | add `ScopeHardDeletedSuccessfully` |
| Shared / Map | `Shared/Messages/ScopeMessageMap.cs` (edit) | map `ScopeHardDeletedSuccessfully` → 200 OK (`ScopeNotFound` → 404 already exists) |
| Presentation | `WebApi/Controllers/ScopeController.cs` (edit) | `DELETE {id:guid}/hard`, sets `command.Id = id`, `[RoleRequirement(SystemAdmin)]` |
| DI | `WebApi/Startup.cs` (edit) | register `ICommandHandlerAsync<HardDeleteScopeCommand, HardDeleteScopeCommandOutput>` (no validator) |
| Requirements | `docs/requirements/Use Case Specification Document.md` (edit) | UC-05 main flow + postcondition mention Google Users |

Reader and writer repositories share the scoped `AppDbContext`, so entities loaded through the reader
are the ones removed through the writer — the same reader/writer split UC-01/UC-03/UC-04 use.

## Testing (Testing Spec §6–§7)

Unit tests use `AsyncFakeRepository<T>` (ArturRios.Util.Test) — one instance is both reader and writer
— with **Bogus** for entity building. No input validator, so no Moq stub is needed. Because the handler
touches four aggregates (Scope, Person, GoogleUser, Application), each test wires the relevant fakes and
the membership/scope links between them. Join rows (`SCOPE_OWNER` / `SCOPE_USER`) are not repository-
backed and are asserted only in the functional tests, against the real database cascade.

**Unit — `HardDeleteScopeCommandHandlerTests` (`Command.Tests`)**, GWT naming:
- main flow, no members → scope removed from the store, `0/0/0` counts, `ScopeHardDeletedSuccessfully`.
- main flow, with members → scope + its `SCOPE_USER` persons + Google Users + applications removed from
  their stores; counts equal member totals.
- main flow, a member already individually logically deleted → still counted and still removed.
- main flow, scope already logically deleted → still hard-deleted successfully.
- AF-05a (missing) → `ScopeNotFound`, no deletions.

**Functional — `ScopeControllerHardDeleteTests` (`WebApi.Tests`)**, Testcontainers PostgreSQL, assert
response **and** DB state, using the established `TestTokens` JWT helper + `WebApiTest.Authorize`:
- main flow: System Admin `DELETE …/hard` → 200; the `scope` row, the seeded User person rows, the
  Google User row, the application row, and the scope's `SCOPE_OWNER` / `SCOPE_USER` join rows are all
  gone; the **owner (ScopeAdmin) person row still exists**; counts match seeded totals.
- AF-05a: unknown id → 404.
- a logically-deleted scope can still be hard-deleted → 200; rows gone.
- auth: non-System-Admin (`User` role token) → 403; unauthenticated → 401.

## Out of scope / non-goals

- No migration (no schema change).
- Owner **person** records (`ScopeAdmin`s) are intentionally never deleted; only their `SCOPE_OWNER`
  join rows go, via the scope-delete cascade.
- No logical deletion (that is UC-04) and no restore flow.
