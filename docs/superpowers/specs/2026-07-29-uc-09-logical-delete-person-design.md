# UC-09: Logical Delete Person — Design

## Summary

Implement UC-09 (Logical Delete Person, FR-PE-06 / FR-PE-08): soft-delete a person by setting
`IsDeleted = true`, through one endpoint:

| Method | Endpoint | Actors |
| --- | --- | --- |
| DELETE | `/api/persons/{id}` | System Admin (any person), Scope Admin (`User`s of scopes they own) |

The deletion is a flip of the existing `Person.IsDeleted` column. Per the System Requirements
Document §8, logically deleting a **person** cascades to nothing — the record simply remains in the
database and drops out of default queries (FR-PE-08, already honoured by UC-07's query handlers).

**No schema change / no EF migration:** `person` already carries `IsDeleted` from `InitialCreate`.

This is a write flow mirroring UC-04 (Logical Delete Scope) for its idempotent-lookup shape and UC-08
(Update Person) for its per-actor authorization, `IActorScoped` plumbing, and NFR-12 guard.

## Decisions

1. **The lookup does not filter `IsDeleted`.** AF-09b requires an already-deleted person to return
   `200 OK` idempotently, so the person must be findable in any deletion state — the same choice
   UC-04 made, and the opposite of UC-08, whose AF-08a folds "deleted" into 404.
2. **Authorization is checked before AF-09b is served.** An already-deleted person still runs the
   full per-actor rule and only then returns its idempotent 200. Serving 200 first would let any
   Scope Admin probe for the existence of persons outside their scopes. The check still works on a
   deleted person because logical deletion leaves the `SCOPE_USER` / `SCOPE_OWNER` join rows intact.
3. **Deleting the last owner of a scope is `409 Conflict`** (decided with the user). UC-09 enumerates
   no such flow and NFR-12's text names only *removing* an owner and *hard*-deleting the last owning
   person. But a soft-deleted `ScopeAdmin` can no longer authenticate, so a scope whose only owner is
   soft-deleted is effectively ownerless — exactly what NFR-12 exists to prevent. UC-08 resolved the
   identical tension the same way (its Decision 4), so this reuses `ScopeWouldLoseLastOwner` and its
   409 mapping rather than inventing a second vocabulary for one invariant.
4. **A person may not delete themselves — `403 Forbidden`** (decided with the user). UC-09 says a
   System Admin "may delete any person", which literally includes themselves; the request is refused
   anyway so an admin cannot lock themselves out with one call. This is an added flow, recorded in
   the specification alongside the others.
5. **No validator.** The only input is the route GUID and the actor, neither of which has a shape rule
   to check — the same call UC-04 made. Business rules needing data access live in the handler.
6. **No cascade.** SRD §8 is explicit that a logically deleted person's record simply remains.
   Applications the person owns are untouched (UC-19 owns those), as are their tokens and join rows.

## Routing

One action added to the existing `PersonController`:

- `[HttpDelete("persons/{id:guid}")]` with `[RoleRequirement((int)Roles.SystemAdmin,
  (int)Roles.ScopeAdmin)]` — the System Requirements endpoint table (§5.2) reads
  "ScopeAdmin (owner)+", so a plain `User` is refused by the attribute (403) and never reaches the
  handler. The data-dependent owner rule is enforced in the handler, mirroring `ListScopePersons`.

The action copies the route `id` and the acting user onto the command via the existing `ApplyActor`,
dispatches through `CommandMediator`, and returns
`ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes)`.

## Command and output

`DeletePersonCommand : BaseCommand, IActorScoped`

| Field | Notes |
| --- | --- |
| `Id` (Guid) | The person's `PublicId`, bound from the route |
| `ActingPersonId`, `ActingRole` | Set by the controller from the token, never bound from the body |

`DeletePersonCommandOutput : CommandOutput`

| Field | Notes |
| --- | --- |
| `Id` (Guid) | The person's `PublicId` |
| `AlreadyDeleted` (bool) | `true` when the request was the idempotent no-op of AF-09b |

`AlreadyDeleted` is what distinguishes the two 200 responses. UC-04 conveyed the same distinction
implicitly through its member counts; a person delete has no counts to report, so the flag is stated
outright rather than leaving the two flows indistinguishable to the caller.

## Handler

`DeletePersonCommandHandler` returns `DataOutput<DeletePersonCommandOutput?>` and never throws.

1. **Load** the person by `PublicId` in **any** deletion state, including `ScopeMembership` and
   `ScopeOwnerships` (needed by the authorization rule and the NFR-12 guard) → miss is **AF-09a**
   `PersonNotFound` (404).
2. **Refuse self-deletion** when `command.ActingPersonId == person.Id` → 403
   `CannotDeleteSelf` (Decision 4).
3. **Authorize** (UC-09 step 2), allowing the delete when either holds, else 403
   `NotAuthorizedToDeletePerson`:
   - the actor is a System Admin — any person;
   - the actor is a Scope Admin and the person is a `User` whose `SCOPE_USER` scope the actor owns,
     via the existing `IScopeOwnershipChecker`.
4. **AF-09b:** if `person.IsDeleted` is already `true`, write nothing and return success with
   `AlreadyDeleted = true`.
5. **NFR-12 guard** (Decision 3): if the person is a `ScopeAdmin` and any scope they own has no other
   owner → 409 `ScopeWouldLoseLastOwner`. Uses the same "co-owned scope ids" query
   `UpdatePersonCommandHandler` already runs for its role change.
6. **Apply:** set `IsDeleted = true`, stamp `UpdatedAt = DateTime.UtcNow`, persist through the
   `Person` writer.
7. **Return** `PersonDeletedSuccessfully` with `AlreadyDeleted = false`.

Steps 4 and 5 are ordered that way deliberately: an already-deleted owner has already been excluded
from the scope, so re-running the last-owner guard on them would turn a required idempotent success
(AF-09b) into a 409.

## Messages and status map

Added to `PersonMessages` / `PersonMessageMap`:

| Message | Status | Flow |
| --- | --- | --- |
| `PersonDeletedSuccessfully` | 200 OK | main flow and AF-09b |
| `NotAuthorizedToDeletePerson` | 403 Forbidden | authorization denial (not enumerated by UC-09) |
| `CannotDeleteSelf` | 403 Forbidden | Decision 4 |

Reused unchanged: `PersonNotFound` (404, AF-09a) and `ScopeWouldLoseLastOwner` (409, Decision 3).

## Dependency injection

Register `ICommandHandlerAsync<DeletePersonCommand, DeletePersonCommandOutput>` →
`DeletePersonCommandHandler` alongside the existing person registrations. No validator to register
(Decision 5). `IActorScoped` and `IScopeOwnershipChecker` are already wired from UC-07.

## Components

| Layer | File | New/Edit |
| --- | --- | --- |
| Command / Input | `Command/Input/DeletePersonCommand.cs` | new |
| Command / Handlers | `Command/Handlers/DeletePersonCommandHandler.cs` | new |
| Command / Output | `Command/Output/DeletePersonCommandOutput.cs` | new |
| Shared / Messages | `Shared/Messages/PersonMessages.cs`, `PersonMessageMap.cs` | edit |
| Presentation | `WebApi/Controllers/PersonController.cs` | edit (one action) |
| DI | `WebApi/Startup.cs` | edit |
| Docs | `docs/requirements/Use Case Specification Document.md`, `README.md` | edit |

## Documentation update

UC-09's specification is narrower than the behaviour it implies, so it is brought in line in the same
change — as UC-07's and UC-08's were. Alternative flows to add:

| ID | Condition | Outcome |
| --- | --- | --- |
| AF-09c | Actor not authorized to delete the person | 403 Forbidden |
| AF-09d | Actor is the person being deleted | 403 Forbidden |
| AF-09e | Person is a `ScopeAdmin` and is the sole owner of a scope (NFR-12) | 409 Conflict |

The postcondition is also clarified: the person's join rows, tokens, and owned applications are left
untouched (SRD §8), so the record can be reasoned about after deletion.

## Testing (Testing Specification §6–§7)

**Unit — `Command.Tests`**, `DeletePersonCommandHandlerTests`, GWT naming, `AsyncFakeRepository<Person>`
as both reader and writer, Moq for `IScopeOwnershipChecker`:

- main flow, System Admin deleting a `User` → flipped, `AlreadyDeleted = false`;
- main flow, owning Scope Admin deleting a `User` of their scope → flipped;
- main flow, System Admin deleting a `ScopeAdmin` whose scopes have another owner → flipped;
- AF-09a: unknown id → `PersonNotFound`, no write;
- AF-09b: already deleted → success, `AlreadyDeleted = true`, `UpdatedAt` untouched;
- AF-09b for a sole-owner `ScopeAdmin` already deleted → success, **not** 409;
- AF-09c: Scope Admin on a `User` outside their scopes → `NotAuthorizedToDeletePerson`;
- AF-09c: Scope Admin on a `ScopeAdmin` → `NotAuthorizedToDeletePerson`;
- AF-09d: actor deleting themselves → `CannotDeleteSelf`, no write;
- AF-09e: sole-owner `ScopeAdmin` → `ScopeWouldLoseLastOwner`, no write.

No new Domain behavior, so no Domain unit tests (§6.5 — skip anemic entities).

**Functional — `WebApi.Tests`**, `PersonControllerDeleteTests`, Testcontainers PostgreSQL, asserting
response **and** database state:

- 200 for a System Admin deleting a `User`, with `person.IsDeleted` true in the database and the
  `scope_user` row still present (no cascade);
- 200 for an owning Scope Admin deleting a `User` of their scope;
- 200 for AF-09b, asserting `UpdatedAt` did not move;
- 404 for an unknown id (AF-09a);
- 403 for a non-owning Scope Admin (AF-09c) and for a Scope Admin targeting a `ScopeAdmin`;
- 403 for self-deletion (AF-09d);
- 409 for the sole-owner `ScopeAdmin` (AF-09e), asserting the row is unchanged;
- 403 for a plain `User` — the `[RoleRequirement]` gate;
- 401 unauthenticated.

## Out of scope / non-goals

- No hard deletion (UC-10) and no restore flow.
- No cascade to applications, tokens, Google Users, or join rows (SRD §8).
- No change to how deleted persons are filtered from queries — UC-07 already implements FR-PE-08.
- No schema change and no migration.
