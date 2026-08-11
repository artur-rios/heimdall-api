# UC-23: Promote User to Scope Owner — Design

## Summary

Implement UC-23 (Promote User to Scope Owner, FR-SC-08/FR-SC-13/FR-RO-03): turn an existing `User` of
a scope into a `ScopeAdmin` co-owner of that same scope — one operation that changes the person's
`RoleId`, deletes their `SCOPE_USER` row, and writes a `SCOPE_OWNER` row.

| Method | Endpoint | Requirement | Handler | Guard |
| --- | --- | --- | --- | --- |
| POST | `/api/scopes/{scopeId}/users/{personId}/promote` | FR-SC-08, FR-SC-13, FR-RO-03 | `PromoteScopeUserCommandHandler` | `[RoleRequirement(SystemAdmin, ScopeAdmin)]` |

The route is the one SRD §5.3 already reserves — *"Promote an existing `User` of the scope to
`ScopeAdmin`, making them a co-owner"*, auth column *SystemAdmin, existing Owner*. Both identifiers
come from the route; the request carries no body.

**No schema change / no EF migration.** `SCOPE_USER` and `SCOPE_OWNER` both already exist with their
composite/unique keys and cascade relationships configured (`ScopeUserDbMap`, `ScopeOwnerDbMap`).

This is the third way a person becomes a scope owner, and the only one that *moves* an existing
person between the two scope relationships:

| | Target | Operation |
| --- | --- | --- |
| UC-06 path c | a brand-new person | create as `ScopeAdmin` + `SCOPE_OWNER` |
| UC-21 | an existing `ScopeAdmin` | add a `SCOPE_OWNER` row |
| **UC-23** | an existing `User` **of this scope** | change role, `SCOPE_USER` → `SCOPE_OWNER` |

## Shape

| Artifact | File | New/Edit |
| --- | --- | --- |
| `PromoteScopeUserCommand` | `…Command/Input/PromoteScopeUserCommand.cs` | new |
| `PromoteScopeUserCommandOutput` | `…Command/Output/PromoteScopeUserCommandOutput.cs` | new |
| `PromoteScopeUserCommandHandler` | `…Command/Handlers/PromoteScopeUserCommandHandler.cs` | new |
| `PersonMessages` / `PersonMessageMap` | `…Shared/Messages/` | edit |
| `PersonController` | `…WebApi/Controllers/PersonController.cs` | edit (one action) |
| DI | `…WebApi/Startup.cs` | edit (handler only) |

`PromoteScopeUserCommand : BaseCommand, IActorScoped` carries `ScopeId` and `PersonId`, both bound
from the route, plus the acting caller for AF-23c. **No validator** — the command has no body, the
same shape UC-19, UC-20 and UC-21 have.

## Handler flow

`PromoteScopeUserCommandHandler` deps: `IAsyncReadOnlyRepository<Scope>`,
`IAsyncReadOnlyRepository<Person>`, `IAsyncRepository<Person>`, `IScopeOwnershipChecker` — the same
four `AddScopeOwnerCommandHandler` uses.

| Step | Behavior | Flow |
| --- | --- | --- |
| 1 | Load the scope by `PublicId == ScopeId` and `!IsDeleted` → else `ScopeNotFound` (404) | AF-23a, UC-23 step 2 |
| 2 | `ActorMayManageScopeAsync(ActingRole, ActingPersonId, scope.Id)` → else `NotScopeOwner` (403) | AF-23c |
| 3 | Load the person by `PublicId == PersonId`, including `ScopeMembership` and `ScopeOwnerships`; must exist and not be logically deleted → else `PersonNotScopeUser` (400) | AF-23b |
| 4 | The person must not already hold `ScopeAdmin` → else `AlreadyScopeAdmin` (409) | AF-23d |
| 5 | The person must hold `User` **and** have a `SCOPE_USER` row for *this* scope → else `PersonNotScopeUser` (400) | AF-23b, UC-23 step 3 |
| 6 | *(open question A)* Email must be unused among `ScopeAdmin`/`SystemAdmin` persons → else `EmailAlreadyExists` (409) | FR-PE-09 |
| 7 | `RoleId = ScopeAdmin`, `ScopeMembership = null`, add `ScopeOwner { scope.Id, person.Id }`, stamp `UpdatedAt`, persist through `personWriter.UpdateAsync` | UC-23 steps 4–5 |
| 8 | Return the updated person with `ScopeUserPromotedSuccessfully` (200) | UC-23 step 6 |

Failures are returned as errors on the `DataOutput<T>` rather than thrown, as every handler before it
does.

## Decisions

1. **The endpoint lives on `PersonController`, not `ScopeController`.** The repository routes by the
   resource the action operates on, and `PersonController` already serves every scope-membership and
   scope-ownership route: `POST /api/scopes/{scopeId}/persons` (UC-06 path a),
   `POST …/owners` (UC-06 path c), `POST …/owners/{personId}` (UC-21), `GET …/persons` and
   `GET …/owners` (UC-07). UC-23 changes a person's role and their two scope join rows, so it belongs
   in the same group and shares `PersonMessageMap`. Neither `SCOPE_USER` nor `SCOPE_OWNER` is an
   independently addressable resource (SRD §4.0), so neither gets a controller of its own.

2. **Order is scope → authorization → person.** An actor who fails AF-23c never learns whether the
   person id exists, what role it holds, or whether it belongs to the scope; the 403 is decided from
   the scope alone. Same ordering as `AddScopeOwnerCommandHandler` and `CreateScopeOwnerCommandHandler`.

3. **AF-23d is checked before AF-23b, and it is the one place this endpoint distinguishes targets.**
   A person holding `ScopeAdmin` also satisfies "not a `User` of that scope", so both flows match and
   the more specific one has to win or AF-23d would be unreachable. That means the endpoint answers
   409 for an existing `ScopeAdmin` and 400 for everything else, which does tell those two apart —
   unlike UC-21 AF-21b, which deliberately collapses three conditions into one answer. The
   justification is the same as UC-22 Decision 4: by this point the caller has already passed the
   ownership check of step 2 and is a System Admin or an owner of this very scope, so the fact that
   some person id belongs to a `ScopeAdmin` is not a secret from them. The specification asks for the
   409 explicitly, so this is following it rather than choosing.

4. **`PersonNotScopeUser` collapses AF-23b's three conditions into one message.** "Not found",
   "logically deleted", and "not a `User` of that scope" are one 400 with one wording — the shape
   UC-21 AF-21b uses, and the reason is the same: a caller who could tell an unknown id from a `User`
   of some *other* scope could enumerate person ids across scopes they do not own.

5. **A logically deleted scope is a 404.** AF-23a names both conditions as one outcome, and every
   scope-scoped handler in the repository (UC-06 path a/c, UC-07, UC-21) filters `!IsDeleted` on the
   scope lookup. Promoting somebody inside a scope that has been withdrawn from service is not
   something UC-23 promises.

6. **A logically deleted person cannot be promoted.** AF-23b names the condition, and the
   precondition repeats it. Consistent with UC-21 AF-21b: a deleted person cannot authenticate
   (FR-AU-07, UC-11 AF-11c), so the ownership granted would be unusable.

7. **The `SCOPE_USER` row is deleted by severing the navigation, and the `SCOPE_OWNER` row is added
   through the same aggregate.** Neither join entity derives from `Entity`, so neither has a
   repository. `UpdatePersonCommandHandler` already deletes a membership row with
   `person.ScopeMembership = null` (the relationship is required and cascades), and
   `AddScopeOwnerCommandHandler` already writes an ownership row with `person.ScopeOwnerships.Add(…)`.
   UC-23 is both of those in one `personWriter.UpdateAsync`, so the two rows move atomically —
   FR-PE-11 is never observably violated.

8. **`UpdatedAt` is stamped.** No database trigger maintains it; `UpdatePersonCommandHandler` stamps
   it by hand and this is equally a mutation of the person record.

9. **The response returns the person, not the two identifiers.** UC-23 step 6 says *"returns the
   updated person"* — unlike UC-21, whose result is a join row with nothing to show. The output
   mirrors `UpdatePersonCommandOutput`'s field set minus `ScopeId`, which would be `null` on every
   response by construction: a promoted person no longer belongs to any scope as a `User`. The scope
   they now own appears in `OwnedScopeIds`. Public identifiers only; internal `bigint` ids never leave
   the data layer (SRD §4.0, NFR-15).

10. **A new output type rather than reusing `UpdatePersonCommandOutput`.** The field sets nearly
    match, but `CreatePersonCommandOutput` is shared across three commands because they are the same
    operation on three paths; UC-08 and UC-23 are different operations, and binding UC-23's response
    contract to UC-08's would make either one's evolution the other's problem.

11. **`[RoleRequirement(SystemAdmin, ScopeAdmin)]` keeps a `User` out; the owner rule is the
    handler's.** A `User` can never satisfy "System Admin or existing owner", so the attribute refuses
    them without a query. Whether a *Scope Admin* owns this particular scope is data-dependent and
    therefore `IScopeOwnershipChecker`'s — the same split UC-21 and UC-06 path c make.

12. **The route says `users`, not `persons`.** `PersonController`'s other scope routes use
    `scopes/{scopeId}/persons`, but SRD §5.3 and the UC-23 main flow both write
    `/api/scopes/{id}/users/{personId}/promote`. The specification is explicit and the segment reads
    correctly here — only a `User` can be promoted — so it is followed rather than normalized. No
    routing ambiguity: no other action matches that template.

13. **FR-PE-11 is satisfied in both directions.** The person ends as a `ScopeAdmin` owning at least
    one scope and belonging to none as a `User` — exactly what the invariant asks. SRD §8 names UC-23
    as one of the scope-assignment operations that maintain it.

14. **NFR-12 needs no guard.** Promotion only ever *adds* an owner. No scope can lose its last one.

## Open questions for the gate

**A. FR-PE-09 — the promotion moves the person's email between two uniqueness namespaces.** A
`User`'s email is unique among the `User`s of their scope; a `ScopeAdmin`'s is unique among all
`ScopeAdmin`/`SystemAdmin` persons system-wide, and FR-PE-09 states the two namespaces are
independent. So a `User` whose address already belongs to some admin can be promoted today into a
state FR-PE-09 forbids, and there is no database index to stop it — `PersonDbMap` indexes `Email`
non-uniquely and every uniqueness rule in this system is enforced in the application layer
(`CreateScopeOwnerCommandHandler`, `UpdatePersonCommandHandler.EmailTakenAsync`).

UC-23 defines no alternative flow for it. **Recommendation: add step 6 above** — the same
case-insensitive admin-namespace check `CreateScopeOwnerCommandHandler` already performs, refusing
with the existing `EmailAlreadyExists` (409). It costs one query, reuses a mapped message, and keeps
UC-08's role change and UC-23's from disagreeing about the same invariant. **If you would rather keep
UC-23 to exactly the four flows the specification lists, say so and step 6 comes out** — but then a
promotion can produce a duplicate admin email, and the specification should probably gain an
alternative flow instead.

**B. UC-22 is designed but not implemented.** `main` carries `docs: add uc-22 design and plan` with
no `feat:` commit behind it, and issue [#23](https://github.com/artur-rios/heimdall-api/issues/23)
is still open. UC-23 does not depend on it — this is only a note that the ownership use cases are
being taken out of order.

## Alternative flows → failure paths

| Flow | Condition | Path | Response |
| --- | --- | --- | --- |
| AF-23a | Unknown scope, or a logically deleted one | scope lookup returns `null` | `404` `Scope not found.` |
| AF-23b | Person unknown, logically deleted, not a `User`, or a `User` of a different scope | person/membership lookup fails | `400` `The person must be an existing, non-deleted User of this scope.` |
| AF-23c | Scope Admin acting on a scope they do not own | `IScopeOwnershipChecker` returns `false` | `403` `You are not an owner of the target scope.` |
| AF-23d | The person already holds `ScopeAdmin` | role check | `409` `Person already holds the ScopeAdmin role.` |
| (open question A) | The email is already an admin address | admin-namespace check | `409` `A person with this email already exists.` |
| (precondition) | Caller holds `User` | `[RoleRequirement]` (framework) | `403` |
| (precondition) | Not authenticated | middleware | `401` |

## Messages and status map

Added to `PersonMessages` / `PersonMessageMap`:

| Message | Value | Status | Flow |
| --- | --- | --- | --- |
| `ScopeUserPromotedSuccessfully` | `"Person promoted to scope owner successfully."` | 200 | main flow |
| `PersonNotScopeUser` | `"The person must be an existing, non-deleted User of this scope."` | 400 | AF-23b |
| `AlreadyScopeAdmin` | `"Person already holds the ScopeAdmin role."` | 409 | AF-23d |

`AlreadyScopeAdmin` takes the specification's quoted wording verbatim — UC-23 names the string, which
UC-22 AF-22b did not.

Reused: `ScopeNotFound` (404) for AF-23a, `NotScopeOwner` (403) for AF-23c, and — if open question A
is approved — `EmailAlreadyExists` (409). All three are already mapped.

## Endpoint wiring

One action added to the existing `PersonController` (route `api`):

```csharp
[HttpPost("scopes/{scopeId:guid}/users/{personId:guid}/promote")]
[RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
public async Task<ActionResult<DataOutput<PromoteScopeUserCommandOutput?>>> PromoteScopeUser(
    Guid scopeId, Guid personId)
```

It builds the command from the two route values, calls `HttpContext.ApplyActor(command)` for AF-23c,
dispatches through `CommandMediator`, and returns
`ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes)`.

DI in `Startup.AddDependencies`:

- `ICommandHandlerAsync<PromoteScopeUserCommand, PromoteScopeUserCommandOutput>` →
  `PromoteScopeUserCommandHandler`

## Test coverage

Per Testing Specification §6–§7: `AsyncFakeRepository<T>` for repositories, Moq for
`IScopeOwnershipChecker`, GWT naming with `// Given / // When / // Then`. No validator, so no
validator test class.

**Unit — `PromoteScopeUserCommandHandlerTests`:** the main flow for a System Admin actor and for an
owner actor, asserting all three effects (role changed, membership gone, ownership added) and that
`UpdatedAt` moved; the output carrying public identifiers only and reporting the owned scope;
AF-23a for an unknown and a logically deleted scope; AF-23b for an unknown person, a logically
deleted `User`, a `User` of a *different* scope, and a `SystemAdmin` target; AF-23c for a Scope Admin
the checker rejects; AF-23d for a `ScopeAdmin` target, asserting the existing ownership rows are
untouched; the ordering guarantee of Decision 2 — an unauthorized actor naming a nonexistent person
is refused with AF-23c, not AF-23b; and, if open question A is approved, the duplicate-admin-email
refusal plus a promotion whose address collides only with a `User` of another scope *succeeding*
(the namespaces are independent). Every refusal also asserts the person's role, membership, and
ownerships are unchanged.

**Functional — `PersonControllerPromoteScopeUserTests`:** System Admin → 200, and the database shows
`role_id = ScopeAdmin`, no `scope_user` row, one `scope_owner` row; an owner Scope Admin → 200; a
Scope Admin who owns a *different* scope → 403 with the rows untouched; `User` role → 403; unknown
scope → 404; logically deleted scope → 404; unknown person → 400; a `User` of another scope → 400; a
`ScopeAdmin` target → 409; a repeated call → 409 (the first promotion makes the second hit AF-23d);
no token → 401. Refusals assert the `scope_user` row survives and no `scope_owner` row appeared.

## Not in scope

- **Demoting an owner back to `User`** — no use case defines it.
- **Removing an owner** — UC-22, designed and not yet implemented.
- **Adding an existing `ScopeAdmin` as owner** — UC-21, already implemented.
- **Creating a brand-new `ScopeAdmin` as owner** — UC-06 path c, already implemented.
- Re-issuing a verification email or resetting the password on promotion — neither is named by UC-23;
  `EmailVerified` and the credentials carry over untouched.
- No schema change and no migration.

## Specification note

The use case specification, the SRD endpoint table (§5.3), FR-SC-08/FR-SC-13/FR-RO-03, FR-PE-11,
SRD §8, and GitHub issue [#24](https://github.com/artur-rios/heimdall-api/issues/24) agree on
every point of UC-23: actor list, route, requirements, and the four alternative flows. The one thing
no document settles is the FR-PE-09 namespace move, raised as open question A rather than assumed
silently.
