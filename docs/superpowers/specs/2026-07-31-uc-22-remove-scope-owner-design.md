# UC-22: Remove Scope Owner — Design

## Summary

Implement UC-22 (Remove Scope Owner, FR-SC-08/FR-SC-10): drop the `SCOPE_OWNER` row linking a person
to a scope, provided the scope keeps at least one owner (NFR-12).

| Method | Endpoint | Requirement | Handler | Guard |
| --- | --- | --- | --- | --- |
| DELETE | `/api/scopes/{scopeId}/owners/{personId}` | FR-SC-08, FR-SC-10 | `RemoveScopeOwnerCommandHandler` | `[RoleRequirement(SystemAdmin, ScopeAdmin)]` |

The route is the one SRD §5.3 already reserves — *"Remove an owner from the scope (at least one must
remain)"*, auth column *SystemAdmin, existing Owner* — and the exact inverse of UC-21's `POST`. Both
identifiers come from the route; the request carries no body.

**No schema change / no EF migration.** `SCOPE_OWNER` already exists with the composite key
`(ScopeId, PersonId)` and both cascade relationships configured (`ScopeOwnerDbMap`).

## Shape

| Artifact | File | New/Edit |
| --- | --- | --- |
| `RemoveScopeOwnerCommand` | `…Command/Input/RemoveScopeOwnerCommand.cs` | new |
| `RemoveScopeOwnerCommandOutput` | `…Command/Output/RemoveScopeOwnerCommandOutput.cs` | new |
| `RemoveScopeOwnerCommandHandler` | `…Command/Handlers/RemoveScopeOwnerCommandHandler.cs` | new |
| `PersonMessages` / `PersonMessageMap` | `…Shared/Messages/` | edit |
| `PersonController` | `…WebApi/Controllers/PersonController.cs` | edit (one action) |
| DI | `…WebApi/Startup.cs` | edit (handler only) |

`RemoveScopeOwnerCommand : BaseCommand, IActorScoped` carries `ScopeId` and `PersonId`, both bound
from the route, plus the acting caller for AF-22c. **No validator** — the command has no body, the
same shape UC-19, UC-20 and UC-21 have.

## Handler flow

`RemoveScopeOwnerCommandHandler` deps: `IAsyncReadOnlyRepository<Scope>`,
`IAsyncReadOnlyRepository<Person>`, `IAsyncRepository<Person>`, `IScopeOwnershipChecker` — the same
four `AddScopeOwnerCommandHandler` uses.

| Step | Behavior | Flow |
| --- | --- | --- |
| 1 | Load the scope by `PublicId == ScopeId` and `!IsDeleted` → else `ScopeNotFound` (404) | AF-22a |
| 2 | `ActorMayManageScopeAsync(ActingRole, ActingPersonId, scope.Id)` → else `NotScopeOwner` (403) | AF-22c |
| 3 | Load the person by `PublicId == PersonId`, including `ScopeOwnerships`; the person must exist **and** hold an ownership row for this scope → else `PersonNotScopeOwner` (404) | AF-22a, main flow step 2 |
| 4 | Some *other*, non-deleted person must own the scope → else `ScopeWouldLoseLastOwner` (409) | AF-22b, main flow step 3 |
| 5 | Remove the row from `person.ScopeOwnerships` and persist through `personWriter.UpdateAsync` | main flow step 4 |
| 6 | Return `{ ScopeId, PersonId }` with `ScopeOwnerRemovedSuccessfully` (200) | main flow step 5 |

Failures are returned as errors on the `DataOutput<T>` rather than thrown, as every handler before it
does.

## Decisions

1. **The endpoint lives on `PersonController`, not `ScopeController`.** UC-21's design already
   reserved this spot: the repository routes by the resource at the *tail* of the path, and
   `PersonController` serves `POST /api/scopes/{scopeId}/owners` (UC-06 path c),
   `GET …/owners` (UC-07), and `POST …/owners/{personId}` (UC-21). UC-22 is the fourth route in that
   group; all four share `PersonMessageMap`. `SCOPE_OWNER` is not an independently addressable
   resource (SRD §4.0), so it gets no controller of its own.

2. **Order is scope → authorization → person → last-owner guard.** An actor who fails AF-22c never
   learns whether the person id exists or whether they own the scope; the 403 is decided from the
   scope alone. Same ordering as `AddScopeOwnerCommandHandler` and `CreateScopeOwnerCommandHandler`.

3. **A logically deleted scope is a 404.** AF-22a names only "scope not found", but the lookup
   filters `!IsDeleted` anyway: AF-21a states the two conditions answer alike for the symmetric
   operation, and every scope-scoped handler in the repository (UC-06 path c, UC-07, UC-21) treats a
   logically deleted scope as absent. Rewriting the ownership of a scope that has been withdrawn from
   service is not something UC-22 promises. *Raised as an assumption — the alternative flow's wording
   does not settle it.*

4. **AF-22a's two conditions get two messages, both 404.** "Scope not found" and "the person is not
   an owner of it" reach the same status, but by then the caller has already passed the ownership
   check of step 2 and is entitled to know which one it was, so nothing leaks by distinguishing them.
   `ScopeNotFound` is reused; `PersonNotScopeOwner` (*"The person is not an owner of this scope."*) is
   new. Contrast UC-21 AF-21b, which deliberately collapses three conditions into one answer — there
   the person is *not yet* related to the scope, so telling them apart would let the endpoint probe
   which persons exist. Here the person is being named as an existing owner of a scope the caller
   already administers.

5. **The person lookup does not filter `!IsDeleted` and does not check `RoleId`.** What AF-22a asks
   is whether the `SCOPE_OWNER` row exists, and nothing else. A logically deleted `ScopeAdmin` keeps
   their ownership rows (UC-09 cascades nothing), and clearing such a stale row is exactly the
   cleanup this endpoint is for. The role is likewise irrelevant to *removing* a row that already
   exists — FR-SC-08 constrains who may be *added*, which UC-21 enforces.

6. **AF-22b counts only non-deleted co-owners.** The guard asks whether somebody *other* than this
   person, and not logically deleted, owns the scope; if nobody does, the removal is refused. Excluding
   deleted persons is the rule UC-08, UC-09 and UC-10 already apply — a soft-deleted person cannot
   authenticate (FR-AU-07, UC-11 AF-11c), so they do not keep a scope owned. Without that exclusion an
   ownerless scope could be created by removing the only *live* owner while a deleted one remained on
   the row.

7. **AF-22b reuses `ScopeWouldLoseLastOwner` (409) rather than adding a message.** The use case quotes
   *"Cannot remove the last owner of a scope"*, but this is the same NFR-12 refusal the codebase
   already words as *"This change would leave a scope without an owner. Add another owner first."* —
   and UC-10 AF-10b already reuses it for the identical condition reached from a different direction.
   One canonical message per condition is the repository's rule; the quoted wording is the flow
   describing the refusal, not naming a string. *Raised at the gate — trivially swappable for a new
   message if the literal wording is wanted.*

8. **No idempotent path, so no `AlreadyRemoved` flag.** Repeating the call finds no ownership row and
   answers AF-22a's 404. That is the same contrast UC-19 and UC-20 pin between the two application
   deletions: the logical delete repeats as `200`, the hard delete as `404`. UC-22 removes a row
   outright, so it behaves like the latter.

9. **The row is deleted through the `Person` aggregate.** `ScopeOwner` is a join entity that does not
   derive from `Entity`, so it has no repository of its own; `UpdatePersonCommandHandler` already
   deletes ownership rows with `person.ScopeOwnerships.Clear()`. `Remove(...)` on the tracked
   collection followed by `personWriter.UpdateAsync` is the single-row form, and the exact inverse of
   UC-21's `Add(...)`.

10. **FR-PE-11 is deliberately not guarded here.** Removing a person's last ownership row can leave a
    `ScopeAdmin` owning no scope. SRD §8 names UC-22 by name as one of the two operations that may do
    this: the record is left in place because the person may be given another scope next (UC-21), they
    cannot authenticate meanwhile (FR-AU-07), and cleaning the record up is UC-10's job. FR-PE-11 is
    an invariant the *scope-assignment* operations maintain, not one removal preserves.

11. **`[RoleRequirement(SystemAdmin, ScopeAdmin)]` keeps a `User` out; the owner rule is the
    handler's.** A `User` can never satisfy "System Admin or existing owner", so the attribute refuses
    them without a query. Whether a *Scope Admin* owns this particular scope is data-dependent and
    therefore `IScopeOwnershipChecker`'s — the same split UC-21 makes.

12. **A Scope Admin may remove themselves, provided a co-owner remains.** Nothing in UC-22 forbids it,
    and AF-22b already prevents the damaging case (the sole owner walking away). This is not the
    self-deletion UC-09 AF-09d and UC-10 AF-10c refuse: no person record is destroyed, and the actor
    keeps any other scopes they own.

13. **The response carries public identifiers only.** `ScopeId` and `PersonId` are the two
    `PublicId`s; internal `bigint` ids never leave the data layer (SRD §4.0, NFR-15). The removed row
    had no identifier to return.

## Alternative flows → failure paths

| Flow | Condition | Path | Response |
| --- | --- | --- | --- |
| AF-22a | Unknown scope, or a logically deleted one | scope lookup returns `null` | `404` `Scope not found.` |
| AF-22a | Person unknown, or holds no ownership row for this scope | person/ownership lookup finds nothing | `404` `The person is not an owner of this scope.` |
| AF-22b | This is the scope's only live owner | no other non-deleted owner exists | `409` `This change would leave a scope without an owner. Add another owner first.` |
| AF-22c | Scope Admin acting on a scope they do not own | `IScopeOwnershipChecker` returns `false` | `403` `You are not an owner of the target scope.` |
| (precondition) | Caller holds `User` | `[RoleRequirement]` (framework) | `403` |
| (precondition) | Not authenticated | middleware | `401` |

## Messages and status map

Added to `PersonMessages` / `PersonMessageMap`:

| Message | Value | Status | Flow |
| --- | --- | --- | --- |
| `ScopeOwnerRemovedSuccessfully` | `"Scope owner removed successfully."` | 200 | main flow |
| `PersonNotScopeOwner` | `"The person is not an owner of this scope."` | 404 | AF-22a |

Reused: `ScopeNotFound` (404) for AF-22a's scope half, `NotScopeOwner` (403) for AF-22c, and
`ScopeWouldLoseLastOwner` (409) for AF-22b — all three already mapped.

`PersonNotScopeOwner` is a distinct constant from `NotScopeOwner` despite the similar name: the
latter is about the *caller* and answers 403, the former is about the *target* and answers 404.

## Endpoint wiring

One action added to the existing `PersonController` (route `api`):

```csharp
[HttpDelete("scopes/{scopeId:guid}/owners/{personId:guid}")]
[RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
public async Task<ActionResult<DataOutput<RemoveScopeOwnerCommandOutput?>>> RemoveScopeOwner(
    Guid scopeId, Guid personId)
```

It builds the command from the two route values, calls `HttpContext.ApplyActor(command)` for AF-22c,
dispatches through `CommandMediator`, and returns
`ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes)`.

The template matches UC-21's route with a different verb, so the two coexist without ambiguity.

DI in `Startup.AddDependencies`:

- `ICommandHandlerAsync<RemoveScopeOwnerCommand, RemoveScopeOwnerCommandOutput>` →
  `RemoveScopeOwnerCommandHandler`

## Test coverage

Per Testing Specification §6–§7: `AsyncFakeRepository<T>` for repositories, Moq for
`IScopeOwnershipChecker`, GWT naming with `// Given / // When / // Then`. No validator, so no
validator test class.

**Unit — `RemoveScopeOwnerCommandHandlerTests`:** the main flow for a System Admin actor and for a
co-owner actor; the person's *other* ownerships surviving; the output carrying public identifiers
only; AF-22a for an unknown scope, a logically deleted scope, an unknown person, and a person who
owns a different scope; AF-22b for a sole owner and for the case where the only co-owner is logically
deleted; AF-22b *not* firing when a live co-owner remains; a Scope Admin removing themselves while a
co-owner remains; a logically deleted target whose stale row is removable; AF-22c for a Scope Admin
who does not own the scope; and the ordering guarantee of Decision 2 — an unauthorized actor naming a
nonexistent person is refused with AF-22c, not AF-22a. Every refusal also asserts the ownership row
survives.

**Functional — `PersonControllerRemoveScopeOwnerTests`:** System Admin → 200 and the `scope_owner`
row is gone while the co-owner's row survives; a co-owner Scope Admin → 200; a Scope Admin who owns a
*different* scope → 403 and the row survives; `User` role → 403; unknown scope → 404; logically
deleted scope → 404; unknown person → 404; a person who is not an owner of this scope → 404; the sole
owner → 409 and the row survives; a repeated call → 404 with no second removal; no token → 401.
Refusals assert the `scope_owner` row is still there.

## Not in scope

- **Adding an owner** — UC-21, already implemented.
- **Promoting a `User` to owner** — UC-23; that one changes `RoleId` and moves a `SCOPE_USER` row.
- **Creating a brand-new `ScopeAdmin` as owner** — UC-06 path c, already implemented.
- **Listing owners** — UC-07, already implemented.
- Deleting the person left owning nothing — UC-10, by design (Decision 10).
- No schema change and no migration.

## Specification note

The use case specification, the SRD endpoint table (§5.3), FR-SC-08/FR-SC-10, NFR-12, SRD §8, and
GitHub issue [#23](https://github.com/artur-rios/identity-manager-api/issues/23) agree on every point
of UC-22: actor list, route, requirements, and the three alternative flows. Two points the documents
leave open are settled by Decisions 3 (logically deleted scope) and 7 (which 409 message), both
raised for approval rather than assumed silently.
