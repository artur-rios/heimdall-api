# UC-08: Update Person — Design

## Summary

Implement UC-08 (Update Person, FR-PE-05, FR-RO-02, FR-RO-03, FR-RO-05): change a person's name,
email, and — for a System Admin only — their role, through one endpoint:

| Method | Endpoint | Actors |
| --- | --- | --- |
| PUT | `/api/persons/{id}` | System Admin (any person), Scope Admin (Users of scopes they own), User (self) |

An email change re-checks uniqueness per FR-PE-09 and clears `EmailVerified`. A role change is
restricted to the transitions that are fully determined by the request (see Decision 2), and adjusts
scope associations so FR-PE-10 and NFR-12 continue to hold.

**No schema change / no EF migration:** `person`, `scope_user`, and `scope_owner` already exist from
`InitialCreate`.

## Decisions (from brainstorming)

1. **UC-08 governs the role-change rule; FR-RO-05 is satisfied elsewhere.** UC-08 step 3 and AF-08c
   make role changes System-Admin-only, while FR-RO-05 says Scope Admins "shall be able to assign the
   `User` role to persons within that scope". These conflict on their face. UC-08 wins: FR-RO-05 is
   read as already met by UC-06 path (a), where a Scope Admin creates a person in their scope with
   `RoleId = User` — that *is* the Scope Admin assigning the `User` role. UC-08 therefore does not
   let a Scope Admin change any role.
2. **Only role transitions that need no scope choice are supported.** UC-08 step 5 says a role change
   "adjusts scope associations accordingly", but the request carries no scope id, and most
   transitions need one: a `ScopeAdmin` must own at least one scope (FR-PE-11) and a `User` must
   belong to exactly one (FR-PE-02). Only `→ SystemAdmin` is fully determined, because FR-PE-10
   requires a System Admin to have *no* scope association — so the handler drops the person's join
   rows and needs nothing from the caller. Every other transition is rejected with an explicit
   message rather than guessing at a scope; `User → ScopeAdmin` points the caller at UC-23.
3. **An email change resets `EmailVerified` and sends nothing.** Step 4 specifies only the flag.
   Issuing a fresh verification token is UC-14/UC-15's job, and adding it here would mean sending
   mail the specification never asked for.
4. **Stripping a scope's last owner is `409 Conflict`.** Promoting the last owner of a scope to
   `SystemAdmin` would leave it ownerless, which NFR-12 forbids. UC-08 enumerates no flow for this;
   409 matches AF-08b's shape — a well-formed, authorized request that collides with a data-integrity
   invariant — and reuses a status the contract already returns.

## Routing

One action added to the existing `PersonController`:

- `[HttpPut("persons/{id:guid}")]` — **no** `[RoleRequirement]`. The System Requirements endpoint
  table says "ScopeAdmin (owner)+ / self", and a plain `User` must be able to update their own
  record, so every authenticated role can reach the action and the per-actor rule is enforced in the
  handler. This mirrors UC-07's `GET /api/persons/{id}`.

The action binds the body, copies the route `id` and the acting user onto the command via the
existing `ApplyActor`, dispatches through `CommandMediator`, and returns
`ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes)`.

## Command, validator, output

`UpdatePersonCommand : BaseCommand, IActorScoped`

| Field | Notes |
| --- | --- |
| `Id` (Guid) | The person's `PublicId`, bound from the route |
| `Name` (string) | Required; replaced (PUT semantics, as UC-03) |
| `Email` (string) | Required; replaced |
| `RoleId` (int?) | Optional. `null` means "leave the role unchanged" — what every non-System-Admin caller sends |
| `ActingPersonId`, `ActingRole` | Set by the controller from the token, never bound from the body |

`UpdatePersonCommandValidator` checks shape only — `Name` not empty and at most 200 characters,
`Email` not empty and a valid address, and `RoleId`, **when supplied**, a defined `Roles` value.
Business rules needing data access (existence, authorization, uniqueness, ownership) live in the
handler.

`UpdatePersonCommandOutput : CommandOutput` returns `Id` (PublicId), `Name`, `Email`, `Role` (int),
`EmailVerified`, `ScopeId` (Guid?), `OwnedScopeIds`, `CreatedAt`, `UpdatedAt`. As with every person
payload, there is no field for `PasswordHash` or `Salt`.

## Handler

`UpdatePersonCommandHandler` returns `DataOutput<UpdatePersonCommandOutput?>` and never throws.

1. **Validate** the request → 400 on failure.
2. **Load** the person by `PublicId` where `!IsDeleted`, including `ScopeMembership` and
   `ScopeOwnerships` → miss is **AF-08a** `PersonNotFound` (404).
3. **Authorize**, allowing the update when any holds, else 403 `NotAuthorizedToUpdatePerson`:
   - the actor is a System Admin;
   - the actor **is** the person (self);
   - the actor is a Scope Admin and the person is a `User` whose `SCOPE_USER` scope the actor owns.
4. **Role change**, only when `RoleId` is supplied and differs from the current role:
   - actor is not a System Admin → **AF-08c** 403 `RoleChangeRequiresSystemAdmin`;
   - target is not `SystemAdmin` → 400 `UnsupportedRoleTransition`;
   - the person is a `ScopeAdmin` and any scope they own has no other owner → 409
     `ScopeWouldLoseLastOwner` (NFR-12);
   - otherwise apply: set `RoleId`, and drop the `SCOPE_USER` row (from `User`) or every
     `SCOPE_OWNER` row (from `ScopeAdmin`), satisfying FR-PE-10.
5. **Email change**, when the new email differs from the current one (compared case-insensitively):
   - uniqueness per FR-PE-09 against the **resulting** role — a person who just became a System
     Admin is checked system-wide among admins, not against their old scope; a `User` is checked
     among that scope's Users → collision is **AF-08b** `EmailAlreadyExists` (409);
   - set `EmailVerified = false`.
6. **Apply** name and email, stamp `UpdatedAt = DateTime.UtcNow`, persist through the `Person` writer.
7. **Return** the updated person with `PersonUpdatedSuccessfully`.

An unchanged email must not raise a false conflict, so the uniqueness query excludes the person being
updated — the same guard `UpdateScopeCommandHandler` applies to the scope name.

### Removing scope associations

There is no repository for `ScopeUser` or `ScopeOwner`: the generic repository is constrained to
`Entity` (a `long Id`), and both join rows are keyed by a composite `(ScopeId, PersonId)` instead.
The join rows are therefore removed through the tracked `Person` graph — setting
`person.ScopeMembership = null` or clearing `person.ScopeOwnerships`, then updating the person.

This works because `EfRepository.Query()` returns the tracked `DbSet<T>` (the assembly contains no
`AsNoTracking` call at all), reader and writer resolve to the same scoped `DbContext`, and both join
rows are configured required with `DeleteBehavior.Cascade` (`ScopeUserDbMap`, `ScopeOwnerDbMap`) — so
EF Core deletes the severed dependents as orphans on save. The handler must `Include` both
navigations for this to happen.

**Testing consequence:** `AsyncFakeRepository<Person>` is an in-memory list and models no such
cascade, so the unit tests can only assert that the navigation was cleared. That the row actually
disappears from `scope_user` / `scope_owner` is asserted by the functional tests against
Testcontainers PostgreSQL. This split is deliberate and is called out in the plan.

## Messages and status map

Added to `PersonMessages` / `PersonMessageMap`:

| Message | Status | Flow |
| --- | --- | --- |
| `PersonUpdatedSuccessfully` | 200 OK | main flow |
| `NotAuthorizedToUpdatePerson` | 403 Forbidden | authorization denial (not enumerated by UC-08) |
| `RoleChangeRequiresSystemAdmin` | 403 Forbidden | AF-08c |
| `UnsupportedRoleTransition` | 400 Bad Request | Decision 2 |
| `ScopeWouldLoseLastOwner` | 409 Conflict | NFR-12 (Decision 4) |
| `UnknownRole` | 400 Bad Request | invalid `RoleId` |

Reused unchanged: `PersonNotFound` (404, AF-08a), `EmailAlreadyExists` (409, AF-08b), and the four
shape messages `NameRequired`, `NameTooLong`, `EmailRequired`, `EmailInvalid` (400).

`UnknownRole` is new rather than a reuse of `InvalidRole`, whose text is "Role must be ScopeAdmin or
SystemAdmin." — correct for UC-06 path (b), wrong here, where `User` is also a valid value to submit.

## Dependency injection

Register `IValidator<UpdatePersonCommand>` → `UpdatePersonCommandValidator` and
`ICommandHandlerAsync<UpdatePersonCommand, UpdatePersonCommandOutput>` →
`UpdatePersonCommandHandler`, alongside the existing person registrations. `IActorScoped` and
`IScopeOwnershipChecker` are already in `Shared` from UC-07 and need no new wiring.

## Components

| Layer | File | New/Edit |
| --- | --- | --- |
| Command / Input | `Command/Input/UpdatePersonCommand.cs` | new |
| Command / Validation | `Command/Input/Validation/UpdatePersonCommandValidator.cs` | new |
| Command / Handlers | `Command/Handlers/UpdatePersonCommandHandler.cs` | new |
| Command / Output | `Command/Output/UpdatePersonCommandOutput.cs` | new |
| Shared / Messages | `Shared/Messages/PersonMessages.cs`, `PersonMessageMap.cs` | edit |
| Presentation | `WebApi/Controllers/PersonController.cs` | edit (one action) |
| DI | `WebApi/Startup.cs` | edit |
| Docs | `docs/requirements/Use Case Specification Document.md` | edit (see below) |

## Documentation update

UC-08's specification is narrower than the behaviour it implies, so it is brought in line in the same
change — as UC-07's was:

- record that only `→ SystemAdmin` role transitions are supported, and why;
- add the alternative flows the API returns but the use case does not enumerate: invalid input
  (400), an authorization denial that is not a role change (403), an unsupported transition (400),
  and the NFR-12 last-owner conflict (409);
- note the FR-RO-05 reading from Decision 1, so the apparent contradiction does not resurface.

## Testing (Testing Specification §6–§7)

**Unit — `Command.Tests`:**

- `UpdatePersonCommandHandlerTests`: main flow for each permitted actor (System Admin, self, owning
  Scope Admin); AF-08a; AF-08b for a `User` within scope and for an admin system-wide; unchanged
  email is not a conflict; `EmailVerified` cleared on change and untouched otherwise; AF-08c; the
  unsupported transition; the NFR-12 last-owner conflict; a successful `User → SystemAdmin` clearing
  `ScopeMembership`; a successful `ScopeAdmin → SystemAdmin` clearing `ScopeOwnerships` when another
  owner remains; a Scope Admin denied on a `User` outside their scopes; a `User` denied on another
  person; `RoleId = null` leaving the role untouched.
- `UpdatePersonCommandValidatorTests`: each shape rule, including that a `null` `RoleId` passes and an
  undefined one fails.

**Functional — `WebApi.Tests`** (`PersonControllerUpdateTests`, Testcontainers PostgreSQL, asserting
response **and** database state): 200 for a System Admin, a self-updating `User`, and an owning Scope
Admin; 404; 409 duplicate email; 403 for a Scope Admin attempting a role change; 403 for a
non-owning Scope Admin and for a `User` updating someone else; 400 for invalid input and for an
unsupported transition; 409 for the last-owner case; a successful promotion to `SystemAdmin`
asserting the `scope_user` / `scope_owner` row is **gone** from the database; and 401 unauthenticated.

## Out of scope / non-goals

- No password change (not part of UC-08; UC-13 resets passwords).
- No verification email on an email change (Decision 3).
- No role transitions that require choosing a scope (Decision 2) — UC-21 and UC-23 own those.
- No person deletion (UC-09, UC-10).
- No schema change and no migration.
