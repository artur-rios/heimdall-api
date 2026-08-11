# UC-21: Add Scope Owner — Design

## Summary

Implement UC-21 (Add Scope Owner, FR-SC-08/FR-SC-09): link an **existing** `ScopeAdmin` person to a
scope as an additional owner by inserting a `SCOPE_OWNER` row.

| Method | Endpoint | Requirement | Handler | Guard |
| --- | --- | --- | --- | --- |
| POST | `/api/scopes/{scopeId}/owners/{personId}` | FR-SC-08, FR-SC-09 | `AddScopeOwnerCommandHandler` | `[RoleRequirement(SystemAdmin, ScopeAdmin)]` |

Matches the route the use case's sequence diagram draws and the row SRD §5.3 already reserves, with
the auth column it already states — *SystemAdmin, existing Owner*. Both identifiers come from the
route; the request carries no body.

**No schema change / no EF migration.** `SCOPE_OWNER` already exists with the composite key
`(ScopeId, PersonId)` and both cascade relationships configured (`ScopeOwnerDbMap`).

## Shape

| Artifact | File | New/Edit |
| --- | --- | --- |
| `AddScopeOwnerCommand` | `…Command/Input/AddScopeOwnerCommand.cs` | new |
| `AddScopeOwnerCommandOutput` | `…Command/Output/AddScopeOwnerCommandOutput.cs` | new |
| `AddScopeOwnerCommandHandler` | `…Command/Handlers/AddScopeOwnerCommandHandler.cs` | new |
| `PersonMessages` / `PersonMessageMap` | `…Shared/Messages/` | edit |
| `PersonController` | `…WebApi/Controllers/PersonController.cs` | edit (one action) |
| DI | `…WebApi/Startup.cs` | edit (handler only) |

`AddScopeOwnerCommand : BaseCommand, IActorScoped` carries `ScopeId` and `PersonId`, both bound from
the route, plus the acting caller for AF-21c. **No validator** — the command has no body, exactly as
`DeleteApplicationCommand` and `HardDeleteApplicationCommand` have none.

## Handler flow

`AddScopeOwnerCommandHandler` deps: `IAsyncReadOnlyRepository<Scope>`,
`IAsyncReadOnlyRepository<Person>`, `IAsyncRepository<Person>`, `IScopeOwnershipChecker` — the same
four `CreateScopeOwnerCommandHandler` uses, minus the hashing and e-mail services.

| Step | Behavior | Flow |
| --- | --- | --- |
| 1 | Load the scope by `PublicId == ScopeId` and `!IsDeleted` → else `ScopeNotFound` (404) | AF-21a |
| 2 | `ActorMayManageScopeAsync(ActingRole, ActingPersonId, scope.Id)` → else `NotScopeOwner` (403) | AF-21c |
| 3 | Load the person by `PublicId == PersonId`, `!IsDeleted`, `RoleId == ScopeAdmin`, including `ScopeOwnerships` → else `PersonNotValidScopeAdmin` (400) | AF-21b |
| 4 | Already linked to this scope → return `{ …, AlreadyOwner = true }` with `AlreadyScopeOwner` (200), writing nothing | AF-21d |
| 5 | Add `ScopeOwner { ScopeId = scope.Id }` to `person.ScopeOwnerships` and persist through `personWriter.UpdateAsync` | main flow step 4 |
| 6 | Return `{ ScopeId, PersonId, AlreadyOwner = false }` with `ScopeOwnerAddedSuccessfully` (201) | main flow step 5 |

Failures are returned as errors on the `DataOutput<T>` rather than thrown, as every handler before it
does.

## Decisions

1. **The endpoint lives on `PersonController`, not `ScopeController`.** The repository routes by the
   resource at the *tail* of the path — `ApplicationController` is `[Route("api/scopes/{scopeId}/applications")]`,
   and `PersonController` already serves `POST /api/scopes/{scopeId}/owners` (UC-06 path c) and
   `GET /api/scopes/{scopeId}/owners` (UC-07). UC-21 is the third route in that group and UC-22's
   `DELETE …/owners/{personId}` will be the fourth, so all four stay in one file and share
   `PersonMessageMap`. `SCOPE_OWNER` is not an independently addressable resource (SRD §4.0), so it
   gets no controller of its own.

2. **Order is scope → authorization → person.** An actor who fails AF-21c never learns whether the
   person id exists or what role it holds; the 403 is decided from the scope alone. Same ordering as
   `CreateScopeOwnerCommandHandler`, which checks the scope, then ownership, then the e-mail.

3. **AF-21b collapses three conditions into one 400.** Unknown person, logically deleted person, and
   a person who is a `User` or `SystemAdmin` all answer `The person must be an existing, non-deleted
   ScopeAdmin.` That is exactly what the alternative flow states, and one shared answer keeps the
   endpoint from being used to probe which persons exist. 400 rather than 404: the *addressed*
   resource is the scope's ownership collection, which does exist — it is the referenced person that
   is unusable, so this is a bad request, matching AF-01d's 400 for the same condition in UC-01.

4. **No validator.** Both identifiers are route values typed `Guid` by the route constraint, and
   there is no body — nothing is left for FluentValidation to check. Same shape as UC-19 and UC-20.

5. **The row is written through the `Person` aggregate.** `ScopeOwner` is a keyless-by-convention
   join entity that does not derive from `Entity`, so it has no repository of its own; the codebase
   already mutates ownership through the person (`UpdatePersonCommandHandler` clears
   `ScopeOwnerships` to delete rows, `CreateScopeOwnerCommandHandler` seeds one on creation). Adding
   to the tracked collection and calling `personWriter.UpdateAsync` is the symmetric insert.

6. **AF-21d answers 200 while the main flow answers 201, and the output flags which happened.**
   `ResponseResolver` picks the status from the output's first message, so the two paths carry
   different messages (`AlreadyScopeOwner` vs `ScopeOwnerAddedSuccessfully`) and the status follows.
   `AlreadyOwner` on the output mirrors `DeleteApplicationCommandOutput.AlreadyDeleted`; unlike
   UC-19, here the status already distinguishes the paths, so the flag is confirmation rather than
   the only signal. Nothing is written on the idempotent path — no `UpdatedAt` re-stamp, and no
   attempt to insert a duplicate composite key.

7. **`[RoleRequirement(SystemAdmin, ScopeAdmin)]` keeps a `User` out; the owner rule is the
   handler's.** A `User` can never satisfy "System Admin or existing owner", so the attribute refuses
   them without a query (401/403 covered functionally). Whether a *Scope Admin* owns this particular
   scope is data-dependent and therefore `IScopeOwnershipChecker`'s, exactly as UC-06 AF-06e and
   UC-07 AF-07b.

8. **A logically deleted scope is a 404, not a 403 or a 409.** AF-21a names "not found **or**
   logically deleted" as one outcome, so the lookup filters `!IsDeleted` and both conditions produce
   the same answer — the same call UC-06 path c makes.

9. **FR-PE-11 holds trivially and needs no guard.** The invariant is that a `ScopeAdmin` owns at
   least one scope and belongs to none as a `User`. Step 3 requires the target to already be a
   `ScopeAdmin`, and adding an ownership row only ever increases the count — the invariant cannot be
   broken in this direction, so no last-owner-style check belongs here. (UC-22 is where the guard
   lives, on removal.)

10. **A logically deleted person is refused even though the scope would gain an owner.** Per
    `ScopeOwnershipChecker`'s own reasoning, a deleted person can no longer authenticate, so linking
    them would create an ownership nobody can exercise — and AF-21b names the condition outright.

11. **The response carries public identifiers only.** `ScopeId` and `PersonId` are the two
    `PublicId`s; internal `bigint` ids never leave the data layer (SRD §4.0, NFR-15). There is no
    `SCOPE_OWNER` identifier to return — the row has no `PublicId` by design.

## Alternative flows → failure paths

| Flow | Condition | Path | Response |
| --- | --- | --- | --- |
| AF-21a | Unknown scope, or a logically deleted one | scope lookup returns `null` | `404` `Scope not found.` |
| AF-21b | Unknown person, logically deleted person, or one whose role is not `ScopeAdmin` | person lookup returns `null` | `400` `The person must be an existing, non-deleted ScopeAdmin.` |
| AF-21c | Scope Admin acting on a scope they do not own | `IScopeOwnershipChecker` returns `false` | `403` `You are not an owner of the target scope.` |
| AF-21d | Person already owns the scope | ownership already in `person.ScopeOwnerships` | `200` `Person is already an owner of this scope.` |
| (precondition) | Caller holds `User` | `[RoleRequirement]` (framework) | `403` |
| (precondition) | Not authenticated | middleware | `401` |

## Messages and status map

Added to `PersonMessages` / `PersonMessageMap`:

| Message | Value | Status | Flow |
| --- | --- | --- | --- |
| `ScopeOwnerAddedSuccessfully` | `"Scope owner added successfully."` | 201 | main flow |
| `AlreadyScopeOwner` | `"Person is already an owner of this scope."` | 200 | AF-21d |
| `PersonNotValidScopeAdmin` | `"The person must be an existing, non-deleted ScopeAdmin."` | 400 | AF-21b |

Reused from UC-06: `ScopeNotFound` (404) for AF-21a and `NotScopeOwner` (403) for AF-21c — the same
two answers `CreateScopeOwnerCommandHandler` already gives for the identical conditions.

`ScopeMessages.OwnerNotValidScopeAdmin` is deliberately **not** reused: its wording is plural ("One
or more owners…") because UC-01 validates a list, and it lives in the map `ScopeController` passes,
not the one this endpoint uses.

## Endpoint wiring

One action added to the existing `PersonController` (route `api`):

```csharp
[HttpPost("scopes/{scopeId:guid}/owners/{personId:guid}")]
[RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
public async Task<ActionResult<DataOutput<AddScopeOwnerCommandOutput?>>> AddScopeOwner(
    Guid scopeId, Guid personId)
```

It builds the command from the two route values, calls `HttpContext.ApplyActor(command)` for AF-21c,
dispatches through `CommandMediator`, and returns
`ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes)`.

The template does not collide with the existing `POST scopes/{scopeId:guid}/owners` (UC-06 path c):
the extra `{personId:guid}` segment makes it a distinct, more specific route.

DI in `Startup.AddDependencies`:

- `ICommandHandlerAsync<AddScopeOwnerCommand, AddScopeOwnerCommandOutput>` →
  `AddScopeOwnerCommandHandler`

## Test coverage

Per Testing Specification §6–§7: `AsyncFakeRepository<T>` for repositories, Moq for
`IScopeOwnershipChecker`, GWT naming with `// Given / // When / // Then`. No validator, so no
validator test class.

**Unit — `AddScopeOwnerCommandHandlerTests`:** the main flow for a System Admin actor and for an
existing owner; the row landing on the right scope while the person's other ownerships survive; the
output carrying public identifiers only; AF-21a for an unknown scope and for a logically deleted one;
AF-21b for an unknown person, a logically deleted person, a `User`, and a `SystemAdmin`; AF-21c for a
Scope Admin who does not own the scope; AF-21d returning 200 with `AlreadyOwner` set and no duplicate
row; and the ordering guarantee of Decision 2 — an unauthorized actor naming a nonexistent person is
refused with AF-21c, not AF-21b. Every refusal also asserts no ownership row was added.

**Functional — `PersonControllerAddScopeOwnerTests`:** System Admin → 201 and the `scope_owner` row
exists; an existing owner Scope Admin → 201; a Scope Admin who owns a *different* scope → 403 and no
row; `User` role → 403; unknown scope → 404; logically deleted scope → 404; unknown person → 400;
logically deleted person → 400; a `User` person → 400; a repeated call → 200 with exactly one row
still present; no token → 401. Refusals assert the database has no new `scope_owner` row.

## Not in scope

- **Removing an owner** — UC-22, including its last-owner guard (AF-22b).
- **Promoting a `User` to owner** — UC-23; that one changes `RoleId` and moves a `SCOPE_USER` row.
- **Creating a brand-new `ScopeAdmin` as owner** — UC-06 path c, already implemented.
- **Listing owners** — UC-07, already implemented.
- Reassigning applications, or any other consequence of ownership.
- No schema change and no migration.

## Specification note

The use case specification, the SRD endpoint table (§5.3), FR-SC-08/FR-SC-09, and GitHub issue
[#22](https://github.com/artur-rios/heimdall-api/issues/22) agree on every point of UC-21:
actor list, route, requirements, and the four alternative flows. Nothing needed correcting.
