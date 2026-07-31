# UC-18: Update Application — Design

## Summary

Implement UC-18 (Update Application, FR-AP-06): change an application's name and owner.

| Method | Endpoint | Requirement | Handler | Guard |
| --- | --- | --- | --- | --- |
| PUT | `/api/scopes/{scopeId}/applications/{id}` | FR-AP-06 | `UpdateApplicationCommandHandler` | `[RoleRequirement(SystemAdmin, ScopeAdmin)]` |

The endpoint exposes only `PublicId` identifiers — never the internal `bigint` foreign keys
(SRD §4.0) — and stamps `UpdatedAt`, which no database trigger maintains.

**No schema change / no EF migration.** `Name` and `owner_id` are existing columns; nothing in
`ApplicationDbMap` or the `InitialCreate` migration moves.

## Shape

| Artifact | File | New/Edit |
| --- | --- | --- |
| `UpdateApplicationCommand` | `…Command/Input/UpdateApplicationCommand.cs` | new |
| `UpdateApplicationCommandValidator` | `…Command/Input/Validation/UpdateApplicationCommandValidator.cs` | new |
| `UpdateApplicationCommandOutput` | `…Command/Output/UpdateApplicationCommandOutput.cs` | new |
| `UpdateApplicationCommandHandler` | `…Command/Handlers/UpdateApplicationCommandHandler.cs` | new |
| `ApplicationMessages` / `ApplicationMessageMap` | `…Shared/Messages/` | edit |
| `ApplicationController` | `…WebApi/Controllers/ApplicationController.cs` | edit (one action) |
| DI | `…WebApi/Startup.cs` | edit (validator + handler) |

`UpdateApplicationCommand : BaseCommand, IActorScoped` carries `ScopeId` and `Id` (both bound from
the route), `Name`, `OwnerId`, and the `ActingPersonId` / `ActingRole` pair the controller sets from
the bearer token — never bound from the body, exactly as `CreateApplicationCommand` does.

## Handler flow

`UpdateApplicationCommandHandler` deps: `IValidator<UpdateApplicationCommand>`,
`IAsyncReadOnlyRepository<Application>`, `IAsyncReadOnlyRepository<Person>`,
`IAsyncRepository<Application>`.

| Step | Behavior | Flow |
| --- | --- | --- |
| 1 | Validate `Name` and `OwnerId` | main flow step 2 |
| 2 | Load the application by `PublicId == Id`, `Scope.PublicId == ScopeId`, `!IsDeleted` → else `ApplicationNotFound` (404) | AF-18a |
| 3 | System Admin passes; anyone else must be the application's current owner → else `NotAuthorizedToUpdateApplication` (403) | AF-18c |
| 4 | If `OwnerId` differs from the current owner's `PublicId`, the named person must be a non-deleted `ScopeAdmin` holding a `SCOPE_OWNER` row on the application's scope → else `OwnerNotValidForScope` (400) | AF-18b, FR-AP-03 |
| 5 | Apply `Name` / `OwnerId`, stamp `UpdatedAt`, persist | main flow steps 5–6 |
| 6 | Return the updated application with `ApplicationUpdatedSuccessfully` (200) | main flow step 6 |

Failures are returned as errors on the `DataOutput<T>` rather than thrown, as every handler before
it does.

## Decisions

1. **A Scope Admin may transfer an application to another eligible owner.** UC-18 defines exactly
   three alternative flows, and none of them is UC-16's AF-16c ("Scope Admin attempts to set an
   owner other than themself"). Step 4 of the main flow — *"If the owner changes, the system
   verifies the new owner is …"* — sits in the main flow, unqualified by actor, and AF-18b is its
   only refusal. Taken literally, a Scope Admin who owns an application may hand it to a co-owner of
   its scope.

   The asymmetry with UC-16 is defensible on its own terms: creation attributes a *new* application
   to somebody who did not ask for it, whereas an update gives away one the caller already owns, to
   a person FR-AP-03 already accepts as an owner of that scope. The caller can only give, never
   take.

   **Visible consequence:** after transferring, the former owner's next read of that application is
   a `403` (UC-17 Decision 2), and they cannot transfer it back. That is worth knowing, and it is
   the reading the specification supports; the alternative — mirroring AF-16c — would invent a
   fourth alternative flow UC-18 does not define.

2. **Authorization compares the current owner, not scope ownership.** A System Admin updates any
   application; anybody else must *be* the owner. Owning the scope is not by itself grounds to
   modify another owner's application — the same call UC-17 Decision 2 made for reads, and the
   reason `IScopeOwnershipChecker` is not a dependency here. Two co-owners of one scope therefore
   cannot edit each other's applications.

3. **The lookup is qualified by the route's `scopeId`, and every miss is one 404.** An unknown
   application, an application that lives in a *different* scope, an unknown scope id, and a
   logically deleted application all return AF-18a `Application not found.` (404). UC-18 defines
   exactly one 404 flow and the addressed resource genuinely does not exist in all four cases. Same
   shape as UC-17 Decision 3.

4. **Existence is checked before authorization, giving AF-18a priority over AF-18c.** Literal
   reading of the flow order (step 3 authorizes an application the system has already found), and it
   keeps both alternative flows observable. Ids are GUIDs, so the disclosure is that *some*
   application holds that GUID in that scope, nothing more. Same call as UC-07 Decision 3 and UC-17
   Decision 4.

5. **PUT replaces: `Name` and `OwnerId` are both required.** `UpdatePersonCommand` sets the
   precedent — a PUT carries the full resource, and a caller who wants to change only the name
   resubmits the current owner. No PATCH, and no nullable "leave unchanged" fields.

6. **The new owner is verified only when the owner actually changes.** Main flow step 4 says exactly
   that. Verifying unconditionally would refuse an ordinary rename whenever the *existing* owner had
   since been logically deleted or had lost their `SCOPE_OWNER` row — a refusal UC-18 does not
   define, and one that would leave such an application uneditable. Resubmitting the current owner
   is therefore always accepted.

7. **Invalid input is a 400 with no alternative flow of its own.** UC-18's main flow step 2
   validates, but the specification lists no `AF-18d` for it. The validator reuses UC-16's
   `NameRequired` / `NameTooLong` / `OwnerRequired`, which already map to 400; no new message and no
   invented flow id.

8. **A logically deleted application cannot be updated,** per the precondition ("application exists
   and is not logically deleted") and AF-18a. There is no `includeDeleted` on this endpoint —
   restoring an application is not what FR-AP-06 describes, and UC-19/UC-20 own the deletion side.

9. **The scope is not re-validated as active.** UC-04 cascades `IsDeleted = true` from a scope to
   its applications, so an application in a logically deleted scope is itself deleted and step 2
   already refuses it. A separate scope lookup would add a query that can never change the answer.

10. **The acting person is not re-checked as active.** A logically deleted Scope Admin holding a
    not-yet-expired token can still update an application they own, exactly as UC-17 Decision 8
    already lets them read one. Project-wide trade-off (tokens are validated `ClaimsOnly`), not a
    new one; closing it belongs with token revocation.

11. **`UpdateApplicationCommandOutput` is a command output of its own,** carrying `Id`, `Name`,
    `ScopeId`, `OwnerId`, `CreatedAt`, `UpdatedAt`. It is not merged with the query layer's
    `ApplicationOutput` (different project, and that one carries `IsDeleted`) nor with
    `CreateApplicationCommandOutput` (no `UpdatedAt`) — the same separation `UpdateScopeCommandOutput`
    and `UpdatePersonCommandOutput` already keep.

## Alternative flows → failure paths

| Flow | Condition | Path | Response |
| --- | --- | --- | --- |
| AF-18a | Unknown application, wrong scope, unknown scope, or logically deleted | lookup returns `null` | `404` `Application not found.` |
| AF-18b | New owner unknown, logically deleted, not a `ScopeAdmin`, or without a `SCOPE_OWNER` row on the scope | owner query returns `null` | `400` `Owner must be a Scope Admin who owns the target scope.` |
| AF-18c | Scope Admin who does not own the application | owner comparison fails | `403` `You are not allowed to update this application.` |
| AF-18c | Caller holds the `User` role | `[RoleRequirement]` (framework) | `403` |
| (step 2) | `Name` empty or over 200 chars, `OwnerId` empty | validator | `400` |
| (precondition) | Not authenticated | middleware | `401` |

## Messages and status map

Added to `ApplicationMessages` / `ApplicationMessageMap`:

| Message | Value | Status | Flow |
| --- | --- | --- | --- |
| `ApplicationUpdatedSuccessfully` | `"Application updated successfully."` | 200 | main flow |
| `NotAuthorizedToUpdateApplication` | `"You are not allowed to update this application."` | 403 | AF-18c |

Reused from UC-16/UC-17: `ApplicationNotFound` (404) for AF-18a, `OwnerNotValidForScope` (400) for
AF-18b, and `NameRequired` / `NameTooLong` / `OwnerRequired` (400) for step 2.

## Endpoint wiring

One action added to the existing `ApplicationController` (its route already supplies `scopeId`):

```csharp
[HttpPut("{id:guid}")]
[RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
public async Task<ActionResult<DataOutput<UpdateApplicationCommandOutput?>>> Update(
    Guid scopeId, Guid id, [FromBody] UpdateApplicationCommand command)
```

It copies the route `scopeId` / `id` onto the command, calls `HttpContext.ApplyActor(command)`,
dispatches through `CommandMediator`, and returns
`ResponseResolver.Resolve(result, statusMap: ApplicationMessageMap.StatusCodes)`.

A `User` is refused by the attribute: FR-AP-03 lets them own no application, so every `User` request
to this endpoint is a refusal, and stating it at the framework layer matches the other two
application endpoints. AF-18c stays observable on the handler through the non-owning Scope Admin.

DI in `Startup.AddDependencies`:

- `IValidator<UpdateApplicationCommand>` → `UpdateApplicationCommandValidator`
- `ICommandHandlerAsync<UpdateApplicationCommand, UpdateApplicationCommandOutput>` →
  `UpdateApplicationCommandHandler`

## Test coverage

Per Testing Specification §6–§7: `AsyncFakeRepository<T>` for repositories, Moq for the validator,
GWT naming with `// Given / // When / // Then`.

**Unit — `UpdateApplicationCommandHandlerTests`:** main flow renaming as a System Admin and as the
owning Scope Admin; an owner change to an eligible co-owner (Decision 1); resubmitting the current
owner when that owner has since been logically deleted still succeeds (Decision 6); `UpdatedAt` is
stamped and `CreatedAt` is not; AF-18a for an unknown id, an application in a different scope, an
unknown scope id, and a logically deleted application; AF-18c for a Scope Admin who owns the scope
but not the application and for an unrelated Scope Admin; AF-18b for a new owner who is unknown,
logically deleted, a `User`, a System Admin, or a `ScopeAdmin` of a different scope; invalid input
leaves the row untouched.

**Unit — `UpdateApplicationCommandValidatorTests`:** name required, name at and over 200 characters,
owner required, and a fully valid command.

**Functional — `ApplicationControllerUpdateTests`:** System Admin renames → 200 with the row and
`updated_at` moved; owning Scope Admin renames → 200; owning Scope Admin transfers to a co-owner →
200 and the row's `owner_id` moves (Decision 1); Scope Admin owning the scope but not the
application → 403; `User` role → 403; unknown application → 404; application addressed through the
wrong scope → 404; logically deleted application → 404; new owner who is a `User` → 400; new owner
of a different scope → 400; unknown new owner → 400; empty name → 400; a forged `actingRole` in the
body is ignored; no token → 401.

## Not in scope

- **Deleting applications.** UC-19 (logical) and UC-20 (hard).
- **Moving an application between scopes.** FR-AP-02 fixes the scope at creation time and FR-AP-06
  names only the name and the owner; `ScopeId` is a route qualifier here, never a field to write.
- **Restoring a logically deleted application** (Decision 8).
- **Applications as authenticating identities** (client credentials, secrets). Unchanged from UC-16.
- No schema change and no migration.
