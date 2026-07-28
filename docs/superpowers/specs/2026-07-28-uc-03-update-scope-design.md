# UC-03: Update Scope — Design

## Summary

Implement UC-03 (Update Scope, FR-SC-04): let a **System Admin** modify an existing, non-logically-deleted
scope's `Name` and `Description` via `PUT /api/scopes/{id}`. This is a write flow that mirrors the
existing UC-01 (Create Scope) pattern end-to-end (command → validator → handler → output → controller →
messages/status map → DI), plus its unit and functional tests.

No schema change is required: `Name`/`Description` and the unique index on `Name` already exist
(`ScopeDbMap`), so **no EF migration** is needed.

## Scope of the change

- **Actor / auth:** System Admin only (`[RoleRequirement((int)Roles.SystemAdmin)]`).
- **Editable fields:** `Name` and `Description` only. Owners (UC-21/22/23), `GoogleSignInEnabled`
  (UC-24), and `IsDeleted` (UC-04) are out of scope.
- **PUT semantics (decided):** full replace. `Name` and `Description` are both taken from the body;
  an omitted/null `description` **clears** the stored value.
- **Output (decided):** parity with create — returns the same shape as `CreateScopeCommandOutput`
  (`Id`, `Name`, `Description`, `GoogleSignInEnabled`, `OwnerIds`) plus `UpdatedAt`.

## Main flow (spec §UC-03)

1. System Admin sends `PUT /api/scopes/{id}` with `{ name, description }`.
2. Validate input (`Name` required).
3. Load the scope by `PublicId` where `!IsDeleted` (owners eager-loaded for the response).
4. Verify the new name does not collide with **another** scope.
5. Apply `Name`/`Description`, set `UpdatedAt = DateTime.UtcNow`, persist.
6. Return the updated scope (200 OK).

## Alternative flows

| ID | Condition | Outcome | Implementation |
| --- | --- | --- | --- |
| AF-03a | Scope not found or logically deleted | 404 Not Found | Handler: lookup filters `!IsDeleted`; null → `ScopeMessages.ScopeNotFound` (already mapped to 404) |
| AF-03b | Name conflicts with another scope | 409 Conflict | Handler: `AnyAsync(x => x.Name == cmd.Name && x.PublicId != cmd.Id)` → `ScopeMessages.NameAlreadyExists` (already mapped to 409) |
| (validation) | Empty name | 400 Bad Request | `UpdateScopeCommandValidator` → `ScopeMessages.NameRequired` (already mapped to 400) |
| (auth) | Actor not System Admin | 403 Forbidden | `[RoleRequirement]` on the controller action (functional test only) |
| (auth) | Unauthenticated | 401 Unauthorized | `AuthenticationMiddleware` (functional test only) |

The name-conflict check intentionally ignores `IsDeleted` so it matches the DB's unique index on
`Name` (a name held by a logically-deleted scope still collides), turning a would-be DB exception into
a clean 409. Excluding the scope's own `PublicId` means re-saving an unchanged name is not a conflict.

## Components (new unless noted)

| Layer | File | Notes |
| --- | --- | --- |
| Command / Input | `Command/Input/UpdateScopeCommand.cs` | `: BaseCommand` — `Id` (Guid, from route), `Name`, `Description?` |
| Command / Validation | `Command/Input/Validation/UpdateScopeCommandValidator.cs` | `Name` NotEmpty → `NameRequired` |
| Command / Handler | `Command/Handlers/UpdateScopeCommandHandler.cs` | deps: `IValidator<UpdateScopeCommand>`, `IAsyncReadOnlyRepository<Scope>`, `IAsyncRepository<Scope>` |
| Command / Output | `Command/Output/UpdateScopeCommandOutput.cs` | create-parity fields + `UpdatedAt` |
| Shared / Messages | `Shared/Messages/ScopeMessages.cs` (edit) | add `ScopeUpdatedSuccessfully` |
| Shared / Map | `Shared/Messages/ScopeMessageMap.cs` (edit) | map `ScopeUpdatedSuccessfully` → 200 OK |
| Presentation | `WebApi/Controllers/ScopeController.cs` (edit) | `PUT {id:guid}`, sets `command.Id = id`, `[RoleRequirement(SystemAdmin)]` |
| DI | `WebApi/Startup.cs` (edit) | register validator + `ICommandHandlerAsync<UpdateScopeCommand, UpdateScopeCommandOutput>` |

The handler reads the scope through the read-only repository (with `Include(x => x.Owners).ThenInclude(o => o.Person)`
for the response), mutates it, and persists via `scopeWriter.UpdateAsync(scope)` — the same
reader/writer split UC-01 uses. Reader and writer share the scoped `AppDbContext`, so the loaded
entity is the one persisted.

## Testing (Testing Spec §6–§7)

### Unit-test infrastructure — `AsyncFakeRepository` (ArturRios.Util.Test 2.2.0)

The documented `FakeRepository<T>` is sync-only and cannot back handlers that depend on
`IAsyncReadOnlyRepository<T>` / `IAsyncRepository<T>` and call
`.Query().AnyAsync()/.FirstOrDefaultAsync()/.ToListAsync()`. This is solved in the shared
`ArturRios.Util.Test` package (v2.2.0), which adds **`AsyncFakeRepository<T>`** — an in-memory
implementation of `IAsyncRepository<T>` (so one instance is both reader and writer) whose `Query()`
returns an **async-capable** queryable (`TestAsyncEnumerable<T>`), letting the EF Core async operators
work without a database.

- Bump `ArturRios.Util.Test` `2.0.0 → 2.2.0` in the test projects (and the Tech Stack doc pin).
- Handler unit tests construct `new AsyncFakeRepository<Scope>()`, seed it via `CreateAsync`, and pass
  it as **both** the reader and writer argument. Entities are built with **Bogus**; the input
  validator collaborator is stubbed with **Moq**.

This keeps the handler on the production `.Query().AnyAsync()` pattern, identical to UC-01, with no
in-repo test shim and no change to the handler for the sake of the fake.

**Unit — `UpdateScopeCommandHandlerTests` (`Command.Tests`)**, GWT naming, `AsyncFakeRepository`/Moq/Bogus:
- main flow → success, data reflects new name/description, `ScopeUpdatedSuccessfully`.
- AF-03a (missing/deleted) → `ScopeNotFound`.
- AF-03b (name collides with another scope) → `NameAlreadyExists`.
- validation (empty name) → `NameRequired`, no write.
- unchanged-name (same scope keeps its name) → success, no false conflict.

**Functional — `ScopeControllerTests` (`WebApi.Tests`)**, Testcontainers PostgreSQL, assert response
**and** DB state:
- main flow: System Admin `PUT` → 200; row's `Name`/`Description` updated; `UpdatedAt` advanced.
- AF-03a: unknown id → 404; deleted scope → 404.
- AF-03b: existing other name → 409; DB unchanged.
- validation: empty name → 400.
- auth: non-System-Admin → 403; unauthenticated → 401.

**New test infrastructure — functional JWT minting.** UC-11 (Login) is not implemented, so there is no
auth route for `AuthenticateAsync`. Functional tests will mint the app's own HMAC JWT directly via
`JwtHandler.CreateToken(...)` with `id` + `role` claims (`TokenClaimKeys`) signed with the test
secret, and apply it with `WebApiTest.Authorize(token)`. This will live as a small reusable helper
under `WebApi.Tests/Support/` (e.g. `TestTokens`), establishing the authenticated-functional-test
pattern for all subsequent scope use cases. The seeded master System Admin provides a valid
`SystemAdmin` identity; a crafted `User`/`ScopeAdmin`-role token drives the 403 case.

## Out of scope / non-goals

- No migration (no schema change).
- No changes to owners, `GoogleSignInEnabled`, or deletion state.
- No filling-in of the pre-existing empty UC-01/UC-02 test stubs.
