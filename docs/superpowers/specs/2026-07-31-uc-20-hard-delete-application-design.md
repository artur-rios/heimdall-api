# UC-20: Hard Delete Application — Design

## Summary

Implement UC-20 (Hard Delete Application, FR-AP-08): permanently remove an application record from
the database.

| Method | Endpoint | Requirement | Handler | Guard |
| --- | --- | --- | --- | --- |
| DELETE | `/api/scopes/{scopeId}/applications/{id}/hard` | FR-AP-08 | `HardDeleteApplicationCommandHandler` | `[RoleRequirement(SystemAdmin)]` |

Matches the endpoint SRD §5.3 already reserves, with the auth column it already states — *SystemAdmin*
— and the `/hard` suffix UC-05 and UC-10 already use on their own controllers. The response carries
only `PublicId` (SRD §4.0).

**No schema change / no EF migration.** No column, table, or constraint changes.

## Shape

| Artifact | File | New/Edit |
| --- | --- | --- |
| `HardDeleteApplicationCommand` | `…Command/Input/HardDeleteApplicationCommand.cs` | new |
| `HardDeleteApplicationCommandOutput` | `…Command/Output/HardDeleteApplicationCommandOutput.cs` | new |
| `HardDeleteApplicationCommandHandler` | `…Command/Handlers/HardDeleteApplicationCommandHandler.cs` | new |
| `ApplicationMessages` / `ApplicationMessageMap` | `…Shared/Messages/` | edit |
| `ApplicationController` | `…WebApi/Controllers/ApplicationController.cs` | edit (one action) |
| DI | `…WebApi/Startup.cs` | edit (handler only) |

`HardDeleteApplicationCommand : BaseCommand` carries `ScopeId` and `Id`, both bound from the route.
**No `IActorScoped`** (Decision 2) and **no validator** — the command has no body, exactly as
`HardDeleteScopeCommand` has none.

## Handler flow

`HardDeleteApplicationCommandHandler` deps: `IAsyncReadOnlyRepository<Application>`,
`IAsyncRepository<Application>`. Nothing else — no `Person` repository, no `IScopeOwnershipChecker`.

| Step | Behavior | Flow |
| --- | --- | --- |
| 1 | Load the application by `PublicId == Id` and `Scope.PublicId == ScopeId`, **in any deletion state** → else `ApplicationNotFound` (404) | AF-20a |
| 2 | Permanently delete the record. Nothing cascades — no entity carries a foreign key to `application` | main flow step 2 |
| 3 | Return `{ Id }` with `ApplicationHardDeletedSuccessfully` (200) | main flow step 3 |

Failures are returned as errors on the `DataOutput<T>` rather than thrown, as every handler before it
does.

## Decisions

1. **The lookup deliberately omits the `!IsDeleted` filter.** A logically deleted application — by
   UC-19 directly, or by UC-04's cascade from its scope — is exactly what a cleanup pass starts from,
   so it must still be hard-deletable. The same call `HardDeleteScopeCommandHandler` and
   `HardDeletePersonCommandHandler` already make.

2. **No handler-level authorization, and therefore no actor on the command.** UC-20 names exactly one
   actor, System Admin, and its precondition states the `SystemAdmin` role. Unlike UC-17/18/19, there
   is no data-dependent narrowing left for the handler to apply, so `[RoleRequirement]` settles the
   rule completely and the command carries no `ActingPersonId`/`ActingRole` — mirroring
   `HardDeleteScopeCommand`. (`HardDeletePersonCommand` carries the actor only because AF-10c refuses
   a self-deletion; UC-20 has no comparable flow.)

3. **A Scope Admin is refused by the attribute, including for applications they own.** UC-19 lets an
   owning Scope Admin logically delete their own application; UC-20 does not let them purge it. That
   is the specification's actor list, not an omission — permanent removal is a System Admin
   operation, the same split UC-04/UC-05 draw for scopes. AF-20a stays the handler's only flow.

4. **The lookup is qualified by the route's `scopeId`, and all misses are one 404.** An unknown
   application id, an unknown scope id, and an application that lives in a *different* scope all
   return AF-20a `Application not found.` The addressed resource genuinely does not exist in all
   three cases. Same shape as UC-17 Decision 3, UC-18 Decision 3, and UC-19 Decision 4.

5. **Nothing cascades.** `Application` is a leaf in the data model: no entity carries a foreign key to
   `application`, and the two foreign keys it holds point *outward* to its scope and owner, which are
   untouched. Contrast UC-05 and UC-10, where the deleted aggregate is the principal and the handler
   removes dependents explicitly — hence no dependent counts on this output.

6. **No idempotent path — a repeat call is a 404.** UC-20 defines exactly one alternative flow,
   AF-20a, and after a hard delete the row is gone, so the second request finds nothing. This is the
   deliberate difference from UC-19's AF-19b, and matches UC-05 and UC-10, neither of which has an
   idempotent path either.

7. **The scope's own deletion state is not consulted.** A logically deleted scope's applications are
   themselves already flagged (UC-04's cascade), and Decision 1 hard-deletes regardless of the flag,
   so a separate scope lookup would add a query that cannot change the answer. Same as UC-18
   Decision 9 and UC-19 Decision 5.

8. **The output carries the public id only.** There is no dependent total to report (Decision 5) and
   internal `Id` never leaves the data layer (SRD §4.0). Contrast `HardDeleteScopeCommandOutput` and
   `HardDeletePersonCommandOutput`, which report the dependents they removed.

9. **No confirmation step, no soft-delete precondition.** UC-20's main flow is three steps and its
   precondition is only that the application exists — it does **not** require the application to be
   logically deleted first. An active application can be hard-deleted in one call, exactly as UC-05
   and UC-10 allow for scopes and persons.

## Alternative flows → failure paths

| Flow | Condition | Path | Response |
| --- | --- | --- | --- |
| AF-20a | Unknown application, unknown scope, an application of another scope, or a repeat call | lookup returns `null` | `404` `Application not found.` |
| (precondition) | Caller holds `ScopeAdmin` or `User` | `[RoleRequirement]` (framework) | `403` |
| (precondition) | Not authenticated | middleware | `401` |

## Messages and status map

Added to `ApplicationMessages` / `ApplicationMessageMap`:

| Message | Value | Status | Flow |
| --- | --- | --- | --- |
| `ApplicationHardDeletedSuccessfully` | `"Application hard deleted successfully."` | 200 | main flow |

Reused from UC-17: `ApplicationNotFound` (404) for AF-20a. The wording follows
`ScopeHardDeletedSuccessfully` and `PersonHardDeletedSuccessfully`.

## Endpoint wiring

One action added to the existing `ApplicationController` (its route already supplies `scopeId`):

```csharp
[HttpDelete("{id:guid}/hard")]
[RoleRequirement((int)Roles.SystemAdmin)]
public async Task<ActionResult<DataOutput<HardDeleteApplicationCommandOutput?>>> HardDelete(
    Guid scopeId, Guid id)
```

It builds the command from the two route values, dispatches through `CommandMediator`, and returns
`ResponseResolver.Resolve(result, statusMap: ApplicationMessageMap.StatusCodes)`. No
`HttpContext.ApplyActor` call — the command has no actor (Decision 2), the same as
`ScopeController.HardDelete`.

DI in `Startup.AddDependencies`:

- `ICommandHandlerAsync<HardDeleteApplicationCommand, HardDeleteApplicationCommandOutput>` →
  `HardDeleteApplicationCommandHandler`

## Test coverage

Per Testing Specification §6–§7: `AsyncFakeRepository<T>` for repositories, GWT naming with
`// Given / // When / // Then`. No validator, so no validator test class.

**Unit — `HardDeleteApplicationCommandHandlerTests`:** the main flow removing an active application
from the store; a logically deleted application removed just the same (Decision 1); the output
carrying public identifiers only; only the addressed application removed, a sibling in the same scope
surviving; AF-20a for an unknown id, for an application in a different scope, and for an unknown scope
id — each asserting the row survives; and a repeat delete reporting AF-20a rather than success
(Decision 6).

**Functional — `ApplicationControllerHardDeleteTests`:** System Admin → 200 and the row is gone from
the database; a logically deleted application → 200 and gone; the owning scope and owner person rows
survive (Decision 5); Scope Admin who owns the application → 403 and the row survives (Decision 3);
`User` role → 403; unknown application → 404; application addressed through the wrong scope → 404 and
the row survives; a repeated call → 404 (Decision 6); no token → 401. Each refusal asserts the row is
still present and its `is_deleted` unchanged.

## Not in scope

- **Logical deletion** — UC-19, and its own route without the `/hard` suffix.
- **Cascading to anything** (Decision 5).
- **Restoring an application** — no use case describes it.
- **Bulk or scope-wide application purges** — UC-05 already removes a scope's applications as part of
  hard-deleting the scope.
- No schema change and no migration.

## Specification note

The use case specification, the SRD endpoint table (§5.3), and GitHub issue
[#21](https://github.com/artur-rios/heimdall-api/issues/21) agree on every point of UC-20:
actor, route, requirement, and the single alternative flow. Nothing needed correcting for this use
case.
