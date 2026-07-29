# UC-10: Hard Delete Person — Design

## Summary

Implement UC-10 (Hard Delete Person, FR-PE-07 / NFR-11 / NFR-12): permanently remove a person and
everything that hangs off them, through one endpoint:

| Method | Endpoint | Actors |
| --- | --- | --- |
| DELETE | `/api/persons/{id}/hard` | System Admin only |

Per the System Requirements Document §8, hard-deleting a person removes their owned applications
(NFR-11), their password reset and email verification tokens, and their `SCOPE_USER` row (a `User`) or
`SCOPE_OWNER` rows (a `ScopeAdmin`) — rejected when that would leave a scope with no owner (NFR-12).
Google Users are untouched: they belong to a scope, not to a person, and cannot own an application.

**No schema change / no EF migration:** every foreign key pointing at `person` is already
`ON DELETE CASCADE` from `InitialCreate` — `application.owner_id`, `password_reset_token.person_id`,
`email_verification_token.person_id`, `scope_user.person_id`, and `scope_owner.person_id`.

This is a write flow mirroring UC-05 (Hard Delete Scope) for its cascade shape and output counts, and
UC-09 (Logical Delete Person) for its actor guard and NFR-12 check.

## Decisions

1. **The lookup does not filter `IsDeleted`.** A logically deleted person must still be hard-deletable —
   soft deletion is exactly the state a cleanup pass starts from. UC-05 made the same choice for an
   already logically deleted scope (its AF-05a note).
2. **Dependents are deleted explicitly, join rows by database cascade** (decided with the user).
   The handler loads and deletes the person's owned applications and both token sets through their
   repositories, then the person row, whose FKs clear `scope_user` / `scope_owner`. Those join rows have
   composite keys and no surrogate `Id`, so no `IAsyncRepository<T>` can address them — the same split
   UC-05 uses. Doing the reachable deletes explicitly keeps the cascade visible to unit tests, which
   run against fakes that know nothing about foreign keys, and gives the response something to report.
3. **A person may not hard-delete themselves — `403 Forbidden`** (decided with the user). UC-10 lets a
   System Admin remove "a person" without excluding themselves; the request is refused anyway so one
   call cannot permanently destroy the caller's own account. UC-09 resolved the identical tension the
   same way (its Decision 4), so this reuses `CannotDeleteSelf`. Recorded as a new AF-10c.
4. **The NFR-12 guard runs even when the target is already logically deleted** (decided with the user).
   UC-09 let its idempotent AF-09b win over the last-owner guard, on the reasoning that a soft-deleted
   owner is already out of the scope. UC-10 takes the stricter reading: NFR-12 names hard-deleting the
   last owning person explicitly, and applying the guard unconditionally keeps "every scope row has at
   least one `scope_owner` row" true in the database. The cost is that removing a soft-deleted sole
   owner takes two steps — add another owner (UC-21) or hard-delete the scope (UC-05) first.
5. **Co-owners must not be logically deleted to count.** The guard reuses UC-09's query, which excludes
   logically deleted persons when gathering co-owned scope ids: a soft-deleted `ScopeAdmin` can no
   longer authenticate, so they do not keep a scope owned.
6. **No validator.** The only input is the route GUID and the actor, neither of which has a shape rule
   to check — the same call UC-04, UC-05 and UC-09 made.
7. **Authorization is entirely the attribute's.** UC-10 permits only System Admins, and that is not
   data-dependent, so there is no handler-side authorization branch and no `IScopeOwnershipChecker`
   dependency. The command still carries the actor, for Decision 3 alone.

## Routing

One action added to the existing `PersonController`:

- `[HttpDelete("persons/{id:guid}/hard")]` with `[RoleRequirement((int)Roles.SystemAdmin)]` — the
  System Requirements endpoint table (§5.2) and the authorization matrix (§7) both read SystemAdmin
  only, so a `ScopeAdmin` or `User` is refused by the attribute (403) and never reaches the handler.
  The `/hard` suffix mirrors `DELETE /api/scopes/{id}/hard` from UC-05.

The action copies the route `id` and the acting user onto the command via the existing `ApplyActor`,
dispatches through `CommandMediator`, and returns
`ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes)`.

## Command and output

`HardDeletePersonCommand : BaseCommand, IActorScoped`

| Field | Notes |
| --- | --- |
| `Id` (Guid) | The person's `PublicId`, bound from the route |
| `ActingPersonId`, `ActingRole` | Set by the controller from the token, never bound from the body. Only `ActingPersonId` is read (Decision 3); `ActingRole` comes with the interface and is enforced by the attribute |

`HardDeletePersonCommandOutput : CommandOutput`

| Field | Notes |
| --- | --- |
| `Id` (Guid) | The removed person's `PublicId` |
| `DeletedApplicationCount` (int) | Applications the person owned, counted regardless of their individual deletion state |
| `DeletedTokenCount` (int) | Password reset **and** email verification tokens, combined — UC-10 step 3 treats them as one step |

Counts follow UC-05's precedent of reporting what the cascade removed, so a caller can see the blast
radius of an irreversible operation.

## Handler

`HardDeletePersonCommandHandler` returns `DataOutput<HardDeletePersonCommandOutput?>` and never throws.

1. **Load** the person by `PublicId` in **any** deletion state, including `ScopeOwnerships` (needed by
   the NFR-12 guard) → miss is **AF-10a** `PersonNotFound` (404).
2. **Refuse self-deletion** when `command.ActingPersonId == person.Id` → 403 `CannotDeleteSelf`
   (**AF-10c**, Decision 3). Checked before the guard below, so a caller targeting themselves gets the
   reason that actually applies to them rather than a last-owner conflict.
3. **NFR-12 guard** (UC-10 step 2): if the person is a `ScopeAdmin` and any scope they own has no other
   non-deleted owner → 409 `ScopeWouldLoseLastOwner` (**AF-10b**). Same query
   `DeletePersonCommandHandler` and `UpdatePersonCommandHandler` already run.
4. **Collect** the dependents: applications where `OwnerId == person.Id`, password reset tokens and
   email verification tokens where `PersonId == person.Id` — all regardless of deletion state.
5. **Delete** applications first, then both token sets, then the person (UC-10 steps 3–6). Applications
   and tokens reference the person, so they go first and no foreign key is ever violated. Persistence
   errors are surfaced as errors on the output.
6. **Return** `PersonHardDeletedSuccessfully` with the person's `PublicId` and the two counts. Deleting
   the person row clears the `SCOPE_USER` / `SCOPE_OWNER` join rows through `ON DELETE CASCADE`
   (UC-10 step 5).

The handler takes reader and writer repositories for `Person`, `Application`, `PasswordResetToken`, and
`EmailVerificationToken`, and reuses UC-05's private `DeleteAllAsync` shape for the bulk removals.

## Messages and status map

Added to `PersonMessages` / `PersonMessageMap`:

| Message | Status | Flow |
| --- | --- | --- |
| `PersonHardDeletedSuccessfully` | 200 OK | main flow |

Reused unchanged: `PersonNotFound` (404, AF-10a), `ScopeWouldLoseLastOwner` (409, AF-10b), and
`CannotDeleteSelf` (403, AF-10c). Three of the four responses already have a canonical message, which
is the point of keeping one vocabulary per entity.

## Dependency injection

Register `ICommandHandlerAsync<HardDeletePersonCommand, HardDeletePersonCommandOutput>` →
`HardDeletePersonCommandHandler` alongside the existing person registrations. No validator
(Decision 6). The generic repositories for `Application`, `PasswordResetToken`, and
`EmailVerificationToken` come from `AddDataConfigFromEnvironment<AppDbContext>`, as UC-05's do.

## Components

| Layer | File | New/Edit |
| --- | --- | --- |
| Command / Input | `Command/Input/HardDeletePersonCommand.cs` | new |
| Command / Handlers | `Command/Handlers/HardDeletePersonCommandHandler.cs` | new |
| Command / Output | `Command/Output/HardDeletePersonCommandOutput.cs` | new |
| Shared / Messages | `Shared/Messages/PersonMessages.cs`, `PersonMessageMap.cs` | edit |
| Presentation | `WebApi/Controllers/PersonController.cs` | edit (one action) |
| DI | `WebApi/Startup.cs` | edit |
| Docs | `docs/requirements/Use Case Specification Document.md`, `README.md` | edit |

## Documentation update

UC-10's specification is narrower than the behaviour it implies, so it is brought in line in the same
change — as UC-07's, UC-08's and UC-09's were:

| ID | Condition | Outcome |
| --- | --- | --- |
| AF-10c | Actor is the person being hard-deleted | 403 Forbidden |

The main flow also gains the explicit endpoint (`DELETE /api/persons/{id}/hard`) and a note that the
lookup finds a person in any deletion state, so a logically deleted person can still be hard-deleted.
A note records Decision 4 — why the last-owner guard applies even to an already soft-deleted target,
where UC-09 lets its idempotent success win.

## Testing (Testing Specification §6–§7)

**Unit — `Command.Tests`**, `HardDeletePersonCommandHandlerTests`, GWT naming, one
`AsyncFakeRepository<T>` per aggregate passed as both reader and writer:

- main flow, System Admin removing a `User` with one application and two tokens → person, application
  and tokens gone, counts 1 and 2;
- main flow, a person with no dependents → removed with zero counts;
- main flow, an already logically deleted person → removed (Decision 1);
- main flow, a `ScopeAdmin` whose every owned scope has another owner → removed;
- another person's application and tokens are left alone;
- AF-10a: unknown id → `PersonNotFound`, nothing removed;
- AF-10b: sole-owner `ScopeAdmin` → `ScopeWouldLoseLastOwner`, nothing removed;
- AF-10b: sole-owner `ScopeAdmin` who is already logically deleted → still refused (Decision 4);
- AF-10c: actor targeting themselves → `CannotDeleteSelf`, nothing removed.

No new Domain behavior, so no Domain unit tests (§6.5 — skip anemic entities). The `[RoleRequirement]`
gate is a functional concern (§6.4).

**Functional — `WebApi.Tests`**, `PersonControllerHardDeleteTests`, Testcontainers PostgreSQL,
asserting response **and** database state:

- 200 for a System Admin removing a `User` who owns an application, has both token kinds, and has a
  `scope_user` row → every one of those rows gone, the scope itself still present;
- 200 for a `ScopeAdmin` whose scope has a second owner → the person and their `scope_owner` row gone,
  the co-owner and the scope surviving;
- 200 for an already logically deleted person (Decision 1);
- 404 for an unknown id (AF-10a);
- 409 for a sole-owner `ScopeAdmin` (AF-10b), asserting the person and their `scope_owner` row survive;
- 403 for self-deletion (AF-10c), asserting the person survives;
- 403 for a `ScopeAdmin` and for a plain `User` — the `[RoleRequirement]` gate;
- 401 unauthenticated.

## Out of scope / non-goals

- No restore flow, and no change to logical deletion (UC-09 owns that).
- No cascade to Google Users — they belong to a scope, not a person (SRD §8), and UC-16 owns their
  removal.
- No application-management endpoints; UC-19 owns hard-deleting an application on its own.
- No schema change and no migration.
