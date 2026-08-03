# UC-24: Enable/Disable Google Sign-In — Design

## Summary

Implement UC-24 (Enable/Disable Google Sign-In, FR-GO-01/FR-GO-02): turn the scope's
`GoogleSignInEnabled` flag on or off, restricted to a System Admin or an existing owner of that
scope.

| Method | Endpoint | Requirement | Handler | Guard |
| --- | --- | --- | --- | --- |
| PUT | `/api/scopes/{id}/google-signin` | FR-GO-01, FR-GO-02 | `SetGoogleSignInCommandHandler` | `[RoleRequirement(SystemAdmin, ScopeAdmin)]` |

The route, the body shape (`{ enabled: bool }`) and the auth column (*SystemAdmin, existing Owner*)
are the ones SRD §5.1 already reserves, and they match the UC-24 main flow diagram exactly.

**No schema change / no EF migration.** `Scope.GoogleSignInEnabled` already exists on the entity, is
mapped with `HasDefaultValue(false)` in `ScopeDbMap`, is in the initial migration, and is already
surfaced by `ScopeOutput`, `CreateScopeCommandOutput` and `UpdateScopeCommandOutput`. FR-GO-01 is
therefore already satisfied by the schema; UC-24 adds only the way to change the flag.

This is the smallest use case in the scope group — two alternative flows, one field written — and it
is the first *write* endpoint on `ScopeController` that is not System-Admin-only.

## Shape

| Artifact | File | New/Edit |
| --- | --- | --- |
| `SetGoogleSignInCommand` | `…Command/Input/SetGoogleSignInCommand.cs` | new |
| `SetGoogleSignInCommandValidator` | `…Command/Input/Validation/SetGoogleSignInCommandValidator.cs` | new — **open question A** |
| `SetGoogleSignInCommandOutput` | `…Command/Output/SetGoogleSignInCommandOutput.cs` | new |
| `SetGoogleSignInCommandHandler` | `…Command/Handlers/SetGoogleSignInCommandHandler.cs` | new |
| `ScopeMessages` / `ScopeMessageMap` | `…Shared/Messages/` | edit |
| `ScopeController` | `…WebApi/Controllers/ScopeController.cs` | edit (one action) |
| DI | `…WebApi/Startup.cs` | edit (handler, and validator if A is approved) |

`SetGoogleSignInCommand : BaseCommand, IActorScoped` carries `Id` (bound from the route, assigned by
the controller as `UpdateScopeCommand.Id` already is), `Enabled` (from the body), plus
`ActingPersonId`/`ActingRole` for AF-24b.

## Handler flow

`SetGoogleSignInCommandHandler` deps: `IValidator<SetGoogleSignInCommand>` (only if open question A
is approved), `IAsyncReadOnlyRepository<Scope>`, `IAsyncRepository<Scope>`, `IScopeOwnershipChecker`.

| Step | Behavior | Flow |
| --- | --- | --- |
| 1 | *(open question A)* Validate `Enabled` was supplied → else `EnabledRequired` (400) | NFR-10 |
| 2 | Load the scope by `PublicId == Id` and `!IsDeleted` → else `ScopeNotFound` (404) | AF-24a, UC-24 step 2 |
| 3 | `ActorMayManageScopeAsync(ActingRole, ActingPersonId, scope.Id)` → else `NotScopeOwner` (403) | AF-24b, UC-24 step 3 |
| 4 | Project the owners' `PublicId`s for the response | UC-24 step 5 |
| 5 | `GoogleSignInEnabled = command.Enabled`, stamp `UpdatedAt`, persist via `scopeWriter.UpdateAsync` | UC-24 step 4 |
| 6 | Return the updated scope with `GoogleSignInUpdatedSuccessfully` (200) | UC-24 step 5 |

Failures are returned as errors on the `DataOutput<T>` rather than thrown, as every handler before
it does.

## Decisions

1. **The endpoint lives on `ScopeController`, not `PersonController`.** The repository routes by the
   resource the action operates on. UC-21 and UC-23 sit on `PersonController` because they change a
   *person*'s role and join rows; UC-24 writes a column on the *scope* and returns the scope. It
   therefore joins `Create`/`Update`/`Delete`/`HardDelete`/`GetById` on `ScopeController` and shares
   `ScopeMessageMap`.

2. **Order is validate → scope → authorization.** An actor who fails AF-24b learns only that the
   scope exists, which they already stated in the URL; there is nothing else in this request to leak.
   The ordering is kept anyway because it is what `AddScopeOwnerCommandHandler`,
   `PromoteScopeUserCommandHandler` and `CreateScopeOwnerCommandHandler` all do, and a reader should
   not have to work out why one handler is different.

3. **A logically deleted scope is a 404 (AF-24a).** The alternative flow says only "scope not
   found", and every scope-scoped handler in the repository filters `!IsDeleted` on the scope lookup
   (UC-03, UC-06 paths a/c, UC-07, UC-21, UC-23). Enabling Google Sign-In on a scope withdrawn from
   service would also contradict FR-GO-13, which refuses Google sign-in for a deleted scope — the
   flag would be set and could never take effect.

4. **`[RoleRequirement(SystemAdmin, ScopeAdmin)]` keeps a `User` out; the owner rule is the
   handler's.** A `User` can never satisfy "System Admin or existing owner", so the attribute refuses
   them without a query. Whether a *Scope Admin* owns this particular scope is data-dependent and
   therefore `IScopeOwnershipChecker`'s — the same split UC-06 path c, UC-21 and UC-23 make. This is
   the first non-System-Admin-only write on `ScopeController`.

5. **`ScopeMessages` gets its own `NotScopeOwner` rather than reusing `PersonMessages`'.**
   `PersonMessages`, `ApplicationMessages` and `ScopeMessages` each declare the messages their own
   controller resolves through, and `NotScopeOwner` is already duplicated verbatim between the first
   two. UC-24 resolves through `ScopeMessageMap`, so the string has to be mapped there; following the
   existing duplication is better than making `ScopeController` reach into `PersonMessages`.

6. **The response returns the whole scope, not just the flag.** UC-24 step 5 and the sequence diagram
   both say `200 OK { scope }`. The output mirrors `UpdateScopeCommandOutput`'s field set — `Id`,
   `Name`, `Description`, `GoogleSignInEnabled`, `OwnerIds`, `CreatedAt`, `UpdatedAt`. Public
   identifiers only (SRD §4.0, NFR-15).

7. **A new output type rather than reusing `UpdateScopeCommandOutput`.** The field sets match today,
   but UC-03 and UC-24 are different operations, and binding UC-24's response contract to UC-03's
   would make either one's evolution the other's problem. Same reasoning as UC-23 Decision 10.
   `IsDeleted` is left out: the handler only ever answers for a non-deleted scope, so the field would
   be `false` on every response by construction.

8. **`UpdatedAt` is stamped.** No database trigger maintains it; `UpdateScopeCommandHandler` stamps
   it by hand and this is equally a mutation of the scope record.

9. **Setting the flag to the value it already holds is a plain 200, not a distinct flow.** UC-24
   defines no alternative flow for it and PUT is idempotent by contract, so the handler writes
   unconditionally rather than short-circuiting. That does stamp `UpdatedAt` on a no-op write, which
   `UpdateScopeCommandHandler` equally does when the name is unchanged — consistent, and cheaper than
   a comparison the specification never asked for. No `AlreadyEnabled` flag on the output: UC-21's
   `AlreadyOwner` exists because AF-21d answers with a *different* status; there is no second status
   here.

10. **The command is named `SetGoogleSignInCommand`.** The repository's verbs are
    Create/Update/Delete/HardDelete/Add/Promote; "Set" is the one that reads correctly for assigning
    a boolean, and it covers both halves of "Enable/Disable" without a second command. The
    alternative, `UpdateGoogleSignInCommand`, is closer to the existing `Update*` family but reads as
    though the Google Sign-In configuration were itself an entity. Say the word at the gate and it is
    a rename.

11. **No `GoogleSignInEnabled` toggle is added to UC-03.** The UC-03 design already states that
    `GoogleSignInEnabled` is not editable through `PUT /api/scopes/{id}` and that UC-24 owns it —
    partly because UC-03 is System-Admin-only while UC-24 admits owners. UC-24 does not change UC-03.

12. **UC-25's `GoogleSignInEnabled = true` precondition is not implemented here.** FR-GO-03 and
    AF-25b belong to UC-25, which is not yet built. UC-24 only makes the flag settable.

## Open questions for the gate

**A. A missing `enabled` in the body is indistinguishable from `enabled: false`.** With a plain
`bool`, `PUT …/google-signin` with body `{}` — or `{ "enabled": null }`, or a typo'd field name —
binds to `false` and *disables* Google Sign-In for the scope. A malformed request would silently
perform the destructive half of the toggle.

UC-24 lists no invalid-input alternative flow, but NFR-10 says all inputs shall be validated, and
UC-01 AF-01b/UC-03 already show input validation producing a 400 for a scope write.

**Recommendation: make `Enabled` a `bool?` and add `SetGoogleSignInCommandValidator` requiring
`NotNull`, refusing with a new `EnabledRequired` (400).** It costs one validator, matches every other
body-carrying command in the repository, and turns a silent disable into an explicit rejection.
**If you would rather keep UC-24 to exactly the two flows the specification lists, say so and step 1
comes out** — `Enabled` becomes a plain `bool`, there is no validator and no DI registration for one,
and an empty body disables the flag.

## Alternative flows → failure paths

| Flow | Condition | Path | Response |
| --- | --- | --- | --- |
| AF-24a | Unknown scope, or a logically deleted one | scope lookup returns `null` | `404` `Scope not found.` |
| AF-24b | Scope Admin acting on a scope they do not own | `IScopeOwnershipChecker` returns `false` | `403` `You are not an owner of the target scope.` |
| (open question A) | `enabled` not supplied | validator | `400` `Enabled is required.` |
| (precondition) | Caller holds `User` | `[RoleRequirement]` (framework) | `403` |
| (precondition) | Not authenticated | middleware | `401` |

## Messages and status map

Added to `ScopeMessages` / `ScopeMessageMap`:

| Message | Value | Status | Flow |
| --- | --- | --- | --- |
| `GoogleSignInUpdatedSuccessfully` | `"Google Sign-In setting updated successfully."` | 200 | main flow |
| `NotScopeOwner` | `"You are not an owner of the target scope."` | 403 | AF-24b |
| `EnabledRequired` | `"Enabled is required."` | 400 | open question A — **only if approved** |

Reused: `ScopeNotFound` (404) for AF-24a — already declared and mapped.

`NotScopeOwner` takes the wording `PersonMessages` and `ApplicationMessages` already use, so the
three controllers answer an unowned scope identically.

## Endpoint wiring

One action added to the existing `ScopeController` (route `api/scopes`):

```csharp
[HttpPut("{id:guid}/google-signin")]
[RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
public async Task<ActionResult<DataOutput<SetGoogleSignInCommandOutput?>>> SetGoogleSignIn(
    Guid id, [FromBody] SetGoogleSignInCommand command)
```

It assigns `command.Id = id` (as `Update` does), calls `HttpContext.ApplyActor(command)` for AF-24b,
dispatches through `CommandMediator`, and returns
`ResponseResolver.Resolve(result, statusMap: ScopeMessageMap.StatusCodes)`.

No routing ambiguity: `{id:guid}/google-signin` cannot collide with `{id:guid}` or `{id:guid}/hard`,
and no other action is a `PUT` under `api/scopes`.

DI in `Startup.AddDependencies`:

- `ICommandHandlerAsync<SetGoogleSignInCommand, SetGoogleSignInCommandOutput>` →
  `SetGoogleSignInCommandHandler`
- `IValidator<SetGoogleSignInCommand>` → `SetGoogleSignInCommandValidator` — only if open question A
  is approved

## Test coverage

Per Testing Specification §6–§7: `FakeRepository<T>` for repositories, Moq for
`IScopeOwnershipChecker` and the validator, Bogus for entity data, GWT naming with
`// Given / // When / // Then`.

**Unit — `SetGoogleSignInCommandHandlerTests`:** enabling and disabling for a System Admin actor and
for an owner actor, asserting the persisted flag and that `UpdatedAt` moved; the output carrying
public identifiers only and reporting the owners; setting the flag to the value it already holds
answering 200 with the flag unchanged (Decision 9); AF-24a for an unknown and for a logically deleted
scope; AF-24b for a Scope Admin the checker rejects; and — if open question A is approved — a missing
`Enabled` refused with `EnabledRequired`. Every refusal also asserts `GoogleSignInEnabled` and
`UpdatedAt` are unchanged.

**Unit — `SetGoogleSignInCommandValidatorTests`** (only if open question A is approved): `true`,
`false` and `null` for `Enabled`.

**Functional — `ScopeControllerSetGoogleSignInTests`:** System Admin enabling → 200 and the database
row shows `google_sign_in_enabled = true`; System Admin disabling an enabled scope → 200 and `false`;
an owner Scope Admin → 200; a Scope Admin who owns a *different* scope → 403 with the flag untouched;
`User` role → 403; unknown scope → 404; logically deleted scope → 404; no token → 401; and — if open
question A is approved — an empty body → 400 with the flag untouched. Refusals assert the persisted
flag did not move, which is the whole point of AF-24b.

## Not in scope

- **Consuming the flag** — FR-GO-03/FR-GO-13 and AF-25b belong to UC-25 (Sign Up / Sign In via
  Google), not yet implemented.
- **Google User records, tokens, or the Google Identity Platform integration** — UC-25 through UC-29.
- **Editing `GoogleSignInEnabled` through UC-03's `PUT /api/scopes/{id}`** — deliberately excluded by
  the UC-03 design.
- No schema change and no migration.

## Specification note

The use case specification, the SRD endpoint table (§5.1), FR-GO-01/FR-GO-02, the scope data model
(§4), the Vision Document, and GitHub issue [#25](https://github.com/artur-rios/identity-manager-api/issues/25)
agree on every point of UC-24: actor list, route, body, requirements, and the two alternative flows.
The one thing no document settles is what a request that omits `enabled` should do, raised as open
question A rather than assumed silently.

**UC-22 is still designed but not implemented** (`docs: add uc-22 design and plan` on `main` with no
`feat:` behind it; issue [#23](https://github.com/artur-rios/identity-manager-api/issues/23) open).
UC-24 does not depend on it — this is only a note that the scope use cases are being taken out of
order.
