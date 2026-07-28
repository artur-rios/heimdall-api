# UC-04: Logical Delete Scope — Design

## Summary

Implement UC-04 (Logical Delete Scope, FR-SC-05 / FR-SC-07): let a **System Admin** soft-delete a
scope via `DELETE /api/scopes/{id}`. Setting the scope's `IsDeleted = true` cascades the logical
deletion to every `User` person belonging to the scope (via `SCOPE_USER`), every Google User in the
scope, and every application in the scope. Scope Admins who own the scope are **not** touched — they
may own other, active scopes.

This is a write flow that mirrors the existing UC-01 (Create) / UC-03 (Update) pattern end-to-end
(command → handler → output → controller → messages/status map → DI), plus unit and functional tests.

No schema change is required: the deletion is a flip of existing `IsDeleted` columns on `Scope`,
`Person`, `GoogleUser`, and `Application`, so **no EF migration** is needed.

## Scope of the change

- **Actor / auth:** System Admin only (`[RoleRequirement((int)Roles.SystemAdmin)]`).
- **Cascade (spec §UC-04 postcondition, §8 deletion strategy):** logical delete of the scope also
  logically deletes its `SCOPE_USER` persons, its Google Users, and its applications. Owners
  (`SCOPE_OWNER`) are left untouched. The Use Case Specification Document's UC-04 section was updated
  to include Google Users so it agrees with §8.
- **No validator:** the only input is the route GUID; there are no body fields to validate (unlike
  UC-01/03 which validate `Name`). YAGNI.
- **Output (decided):** `DeleteScopeCommandOutput` returns the scope's `PublicId` plus
  `DeletedUserCount`, `DeletedGoogleUserCount`, and `DeletedApplicationCount`.

## Count semantics (decided)

`DeletedUserCount` / `DeletedGoogleUserCount` / `DeletedApplicationCount` report the **total** number
of `SCOPE_USER` persons, Google Users, and applications **belonging to the scope**, counted regardless
of their individual deletion state. Both flows compute the counts identically:

- **Main flow:** the cascade flips `IsDeleted = true` (and bumps `UpdatedAt`) only on members not
  already deleted; the returned counts are the totals of all members.
- **AF-04b (already deleted):** nothing is flipped, but the same member totals are still reported.

This matches the requirements update in the Use Case Specification Document (UC-04 postcondition,
main-flow step 5, and AF-04b outcome).

## Main flow (spec §UC-04)

1. System Admin sends `DELETE /api/scopes/{id}`.
2. Load the scope by `PublicId` (any deletion state — the lookup does **not** filter `IsDeleted`,
   so an already-deleted scope is found and handled idempotently).
3. Count the scope's `SCOPE_USER` persons, Google Users, and applications (totals, ignoring their
   `IsDeleted`).
4. Set `scope.IsDeleted = true`, `scope.UpdatedAt = DateTime.UtcNow`, persist.
5. Cascade: flip `IsDeleted = true` + bump `UpdatedAt` on the scope's not-already-deleted `SCOPE_USER`
   persons, Google Users, and applications; persist via `UpdateRangeAsync`.
6. Return `200 OK` with `{ id, deletedUserCount, deletedGoogleUserCount, deletedApplicationCount }`.

## Alternative flows

| ID | Condition | Outcome | Implementation |
| --- | --- | --- | --- |
| AF-04a | Scope not found | 404 Not Found | Handler: lookup returns null → `ScopeMessages.ScopeNotFound` (already mapped to 404) |
| AF-04b | Scope already logically deleted | 200 OK (idempotent) | Handler: if `scope.IsDeleted` is already true, skip all writes, still compute + return member totals with `ScopeDeletedSuccessfully` |
| (auth) | Actor not System Admin | 403 Forbidden | `[RoleRequirement]` on the controller action (functional test only) |
| (auth) | Unauthenticated | 401 Unauthorized | `AuthenticationMiddleware` (functional test only) |

The lookup deliberately omits an `!IsDeleted` filter (unlike UC-03) so AF-04b can be served as an
idempotent success rather than a 404.

## Cascade queries

Persons have no `ScopeId`; membership is the `ScopeUser` join, reciprocated by `Person.ScopeMembership`
(a one-to-one nav). SCOPE_USER persons of the scope are found via:

```
personReader.Query().Where(p => p.ScopeMembership != null && p.ScopeMembership.ScopeId == scope.Id)
```

Google Users and applications each have a direct `ScopeId`, so they are found via
`googleUserReader.Query().Where(g => g.ScopeId == scope.Id)` and
`applicationReader.Query().Where(a => a.ScopeId == scope.Id)`. All filters use the scope's internal
`Id` (never `PublicId`). These translate cleanly in EF Core and evaluate directly against
`AsyncFakeRepository` when tests wire up `Person.ScopeMembership` / `GoogleUser.ScopeId` /
`Application.ScopeId`. Totals are `Count()` over these queries; the actual flips target the subset
where `!IsDeleted`.

## Components (new unless noted)

| Layer | File | Notes |
| --- | --- | --- |
| Command / Input | `Command/Input/DeleteScopeCommand.cs` | `: BaseCommand` — `Id` (Guid, from route) |
| Command / Handler | `Command/Handlers/DeleteScopeCommandHandler.cs` | deps: read-only + writable repositories for `Scope`, `Person`, `GoogleUser`, `Application` |
| Command / Output | `Command/Output/DeleteScopeCommandOutput.cs` | `: CommandOutput` — `Id`, `DeletedUserCount`, `DeletedGoogleUserCount`, `DeletedApplicationCount` |
| Shared / Messages | `Shared/Messages/ScopeMessages.cs` (edit) | add `ScopeDeletedSuccessfully` |
| Shared / Map | `Shared/Messages/ScopeMessageMap.cs` (edit) | map `ScopeDeletedSuccessfully` → 200 OK (`ScopeNotFound` → 404 already exists) |
| Presentation | `WebApi/Controllers/ScopeController.cs` (edit) | `DELETE {id:guid}`, sets `command.Id = id`, `[RoleRequirement(SystemAdmin)]` |
| DI | `WebApi/Startup.cs` (edit) | register `ICommandHandlerAsync<DeleteScopeCommand, DeleteScopeCommandOutput>` (no validator) |

Reader and writer repositories share the scoped `AppDbContext`, so entities loaded through the reader
are the ones persisted through the writer — the same reader/writer split UC-01/UC-03 use.

## Testing (Testing Spec §6–§7)

Unit tests use `AsyncFakeRepository<T>` (ArturRios.Util.Test 2.2.0) — one instance is both reader and
writer — with **Bogus** for entity building. No input validator, so no Moq stub is needed here. Because
the handler touches four aggregates (Scope, Person, GoogleUser, Application), each test wires the
relevant fakes and the membership/scope links between them.

**Unit — `DeleteScopeCommandHandlerTests` (`Command.Tests`)**, GWT naming:
- main flow, no members → scope flipped, `0/0/0` counts, `ScopeDeletedSuccessfully`.
- main flow, with members → scope + its `SCOPE_USER` persons + Google Users + applications flipped to
  `IsDeleted`; counts equal member totals; owners' persons untouched.
- main flow, a member already individually deleted → still counted in totals; not re-bumped.
- AF-04a (missing) → `ScopeNotFound`, no writes.
- AF-04b (already deleted) → success, no writes, member totals still reported.

**Functional — `ScopeControllerDeleteTests` (`WebApi.Tests`)**, Testcontainers PostgreSQL, assert
response **and** DB state, using the established `TestTokens` JWT helper + `WebApiTest.Authorize`:
- main flow: System Admin `DELETE` → 200; scope row `IsDeleted = true`; seeded scope users, Google
  users, and applications `IsDeleted = true`; an owner person row unchanged; counts match seeded
  totals.
- AF-04a: unknown id → 404.
- AF-04b: already-deleted scope → 200; rows unchanged; counts still reflect totals.
- auth: non-System-Admin (`User` role token) → 403; unauthenticated → 401.

## Out of scope / non-goals

- No migration (no schema change).
- Owners (`SCOPE_OWNER` rows and their persons) are intentionally never modified.
- No hard deletion (that is UC-05) and no restore flow.
