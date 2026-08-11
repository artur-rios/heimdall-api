# UC-19: Logical Delete Application — Design

## Summary

Implement UC-19 (Logical Delete Application, FR-AP-07/09): soft-delete an application by setting
`IsDeleted = true`.

| Method | Endpoint | Requirement | Handler | Guard |
| --- | --- | --- | --- | --- |
| DELETE | `/api/scopes/{scopeId}/applications/{id}` | FR-AP-07 | `DeleteApplicationCommandHandler` | `[RoleRequirement(SystemAdmin, ScopeAdmin)]` |

Matches the endpoint SRD §5.3 already reserves, with the auth column it already states — *SystemAdmin
/ ScopeAdmin (owner of the application)*. The response carries only `PublicId` (SRD §4.0), and
`UpdatedAt` is stamped by the handler, as no database trigger maintains it.

**No schema change / no EF migration.** `is_deleted` and `updated_at` are existing columns.

## Shape

| Artifact | File | New/Edit |
| --- | --- | --- |
| `DeleteApplicationCommand` | `…Command/Input/DeleteApplicationCommand.cs` | new |
| `DeleteApplicationCommandOutput` | `…Command/Output/DeleteApplicationCommandOutput.cs` | new |
| `DeleteApplicationCommandHandler` | `…Command/Handlers/DeleteApplicationCommandHandler.cs` | new |
| `ApplicationMessages` / `ApplicationMessageMap` | `…Shared/Messages/` | edit |
| `ApplicationController` | `…WebApi/Controllers/ApplicationController.cs` | edit (one action) |
| DI | `…WebApi/Startup.cs` | edit (handler only) |

`DeleteApplicationCommand : BaseCommand, IActorScoped` carries `ScopeId` and `Id` (both bound from
the route) plus the `ActingPersonId` / `ActingRole` pair the controller sets from the bearer token.
**No validator** — the command has no body, exactly as `DeleteScopeCommand` and
`DeletePersonCommand` have none.

## Handler flow

`DeleteApplicationCommandHandler` deps: `IAsyncReadOnlyRepository<Application>`,
`IAsyncRepository<Application>`. No `IScopeOwnershipChecker`, no `Person` repository (Decision 2).

| Step | Behavior | Flow |
| --- | --- | --- |
| 1 | Load the application by `PublicId == Id` and `Scope.PublicId == ScopeId`, **in any deletion state**, including `Owner` → else `ApplicationNotFound` (404) | AF-19a |
| 2 | System Admin passes; anyone else must be the application's current owner → else `NotAuthorizedToDeleteApplication` (403) | AF-19c |
| 3 | Already `IsDeleted` → return success with `AlreadyDeleted = true`, nothing written | AF-19b |
| 4 | Set `IsDeleted = true`, stamp `UpdatedAt`, persist | main flow step 3 |
| 5 | Return `{ Id, AlreadyDeleted = false }` with `ApplicationDeletedSuccessfully` (200) | main flow step 4 |

Failures are returned as errors on the `DataOutput<T>` rather than thrown, as every handler before it
does.

## Decisions

1. **The lookup deliberately omits the `!IsDeleted` filter.** UC-18 filters it out because a deleted
   application cannot be updated; UC-19 must *find* the deleted one so AF-19b can serve it
   idempotently rather than reporting AF-19a. The same split `DeletePersonCommandHandler` already
   makes against `UpdatePersonCommandHandler`.

2. **Authorization compares the current owner, not scope ownership.** UC-19 step 2 reads "System
   Admin, or the `ScopeAdmin` who owns the application". Owning the *scope* is not by itself grounds
   to delete another owner's application — the same call UC-17 Decision 2 made for reads and UC-18
   Decision 2 for updates, and the reason `IScopeOwnershipChecker` is not a dependency here. Two
   co-owners of one scope cannot delete each other's applications.

3. **Order is AF-19a → AF-19c → AF-19b.** Existence first, so a 403 never doubles as an existence
   oracle beyond "some application holds that GUID in that scope" (ids are GUIDs — same call as UC-17
   Decision 4 and UC-18 Decision 4). Authorization **before** the idempotent path, so an
   already-deleted application cannot be used to probe applications the caller may not see —
   `DeletePersonCommandHandler` orders AF-09c before AF-09b for exactly this reason.

4. **The lookup is qualified by the route's `scopeId`, and both misses are one 404.** An unknown
   application id, an unknown scope id, and an application that lives in a *different* scope all
   return AF-19a `Application not found.` The addressed resource genuinely does not exist in all
   three cases. Same shape as UC-17 Decision 3 and UC-18 Decision 3.

5. **The scope's own deletion state is not consulted.** UC-04 cascades `IsDeleted = true` from a
   scope to its applications, so an application in a logically deleted scope is itself already
   deleted and step 3 serves it as the AF-19b no-op. A separate scope lookup would add a query that
   cannot change the answer. Same as UC-18 Decision 9.

6. **AF-19b is a success, and the response says which path ran.**
   `DeleteApplicationCommandOutput.AlreadyDeleted` distinguishes the two 200s, mirroring
   `DeletePersonCommandOutput`. UC-19 requires the same status and message either way; the flag is
   what makes the two observable in tests without inventing a second message.

7. **On the AF-19b path nothing is written — `UpdatedAt` is not re-stamped.** The record already
   carries the state the request asks for, and moving `UpdatedAt` would misreport when the deletion
   happened. `DeletePersonCommandHandler` and `DeleteScopeCommandHandler` both leave the row
   untouched on their idempotent paths.

8. **Nothing cascades.** FR-AP-07 names the application record alone, and an application owns no
   dependent row in the data model. Contrast UC-04, where the scope cascades *to* applications.

9. **No restore, and no `includeDeleted` switch.** FR-AP-07 describes deletion only; reversing it is
   not in any use case, and UC-20 owns permanent removal.

10. **The acting person is not re-checked as active.** A logically deleted Scope Admin holding a
    not-yet-expired token can still delete an application they own, exactly as UC-17 Decision 8 and
    UC-18 Decision 10 already allow for reads and updates. Project-wide trade-off (tokens are
    validated `ClaimsOnly`); closing it belongs with token revocation.

11. **A `User` is refused by the attribute.** FR-AP-03 lets them own no application, so every `User`
    request to this endpoint is a refusal, and stating it at the framework layer matches the other
    four application endpoints. AF-19c stays observable on the handler through the non-owning Scope
    Admin.

## Alternative flows → failure paths

| Flow | Condition | Path | Response |
| --- | --- | --- | --- |
| AF-19a | Unknown application, unknown scope, or an application of another scope | lookup returns `null` | `404` `Application not found.` |
| AF-19b | Already logically deleted (directly, or by UC-04's cascade) | `IsDeleted` short-circuit | `200` `Application deleted successfully.`, `AlreadyDeleted = true` |
| AF-19c | Scope Admin who does not own the application | owner comparison fails | `403` `You are not allowed to delete this application.` |
| AF-19c | Caller holds the `User` role | `[RoleRequirement]` (framework) | `403` |
| (precondition) | Not authenticated | middleware | `401` |

## Messages and status map

Added to `ApplicationMessages` / `ApplicationMessageMap`:

| Message | Value | Status | Flow |
| --- | --- | --- | --- |
| `ApplicationDeletedSuccessfully` | `"Application deleted successfully."` | 200 | main flow + AF-19b |
| `NotAuthorizedToDeleteApplication` | `"You are not allowed to delete this application."` | 403 | AF-19c |

Reused from UC-17: `ApplicationNotFound` (404) for AF-19a.

## Endpoint wiring

One action added to the existing `ApplicationController` (its route already supplies `scopeId`):

```csharp
[HttpDelete("{id:guid}")]
[RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
public async Task<ActionResult<DataOutput<DeleteApplicationCommandOutput?>>> Delete(
    Guid scopeId, Guid id)
```

It builds the command from the two route values, calls `HttpContext.ApplyActor(command)`, dispatches
through `CommandMediator`, and returns
`ResponseResolver.Resolve(result, statusMap: ApplicationMessageMap.StatusCodes)`.

DI in `Startup.AddDependencies`:

- `ICommandHandlerAsync<DeleteApplicationCommand, DeleteApplicationCommandOutput>` →
  `DeleteApplicationCommandHandler`

## Test coverage

Per Testing Specification §6–§7: `AsyncFakeRepository<T>` for repositories, GWT naming with
`// Given / // When / // Then`. No validator, so no validator test class.

**Unit — `DeleteApplicationCommandHandlerTests`:** main flow as a System Admin and as the owning
Scope Admin; `UpdatedAt` stamped and `CreatedAt` not; the output carries public identifiers only;
AF-19a for an unknown id, an application in a different scope, and an unknown scope id; AF-19c for a
co-owner of the scope who does not own the application and for an unrelated Scope Admin; AF-19b for
an already-deleted application, asserting `AlreadyDeleted = true` and that `UpdatedAt` did not move;
and AF-19c winning over AF-19b when a non-owner addresses an already-deleted application (Decision
3).

**Functional — `ApplicationControllerDeleteTests`:** System Admin → 200 and `is_deleted` flips;
owning Scope Admin → 200; repeating the call → 200 with `AlreadyDeleted = true` and an unmoved
`updated_at`; an application already deleted by UC-04's scope cascade → 200 idempotent; Scope Admin
owning the scope but not the application → 403 and the row still active; `User` role → 403; unknown
application → 404; application addressed through the wrong scope → 404 and the row untouched; no
token → 401. Each refusal asserts the persisted `is_deleted` is unchanged.

## Not in scope

- **Hard deletion** — UC-20, and its own `/hard` route.
- **Restoring a logically deleted application** (Decision 9).
- **Excluding deleted applications from reads** — FR-AP-09 is already implemented by UC-17's
  `includeDeleted` handling; UC-19 only produces the state it filters on.
- **Cascading to anything** (Decision 8).
- No schema change and no migration.

## Specification note

GitHub issue [#20](https://github.com/artur-rios/heimdall-api/issues/20) still carries the
pre-UC-17 wording — it lists `User` among the actors and authorizes "System Admin, Scope Admin of the
scope, or the owning User". The Use Case Specification Document was corrected during UC-17 (see that
design's *Documents corrected* table, row "UC Spec UC-18, UC-19"), and the corrected text is what
this design implements. The issue body is stale and should be refreshed to match.
