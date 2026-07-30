# UC-02: View Scope — Design (completion)

## Summary

UC-02 (View Scope, FR-SC-02/FR-SC-03/FR-SC-07) is partially merged. Both endpoints exist and are
tested:

| Method | Endpoint | Auth (SRD §5.1) | State |
| --- | --- | --- | --- |
| GET | `/api/scopes` | SystemAdmin | done |
| GET | `/api/scopes/{id}` | Authenticated | **missing the per-actor rule** |

What is missing is step 2 of the use case's main flow on the by-id endpoint:

> - System Admin: can view all scopes.
> - Scope Admin: can view only the scopes they own.
> - User: can view only the scope they belong to.

`GET /api/scopes/{id}` currently returns any scope to any authenticated caller, so **AF-02b
(`403 Forbidden`) can never occur**. This design closes that gap and nothing else.

The work was blocked on caller identity and is no longer: UC-11 (Login) established
`IdentityUser`/`IdentityUserMapper` and made `ActingPersonId` the person's `PublicId`, so a handler
can now identify the acting caller (Decision 2 explains why the claims themselves are not the
authority).

## Decisions

1. **The list endpoint needs no per-actor filtering.** SRD §5.1 restricts `GET /api/scopes` to
   `SystemAdmin`, which `[RoleRequirement((int)Roles.SystemAdmin)]` already enforces — a Scope Admin
   or User gets `403` there, which is AF-02b at the role gate, and
   `ScopeControllerViewTests.GivenNonSystemAdmin_WhenGetScopes_ThenForbidden` already covers it.
   Main-flow step 2's per-actor rule therefore governs the by-id path only. The alternative reading —
   open the list endpoint to every actor and filter the page to the scopes they may see — contradicts
   SRD §5.1, so it is not taken here.

2. **The rule is enforced in the handler, against the database, not from the token's claims.**
   UC-11 issues `scopeId`/`ownedScopeIds` claims, but a token outlives the facts it asserts: ownership
   granted or removed (UC-21/UC-22) after issue would leave the claim stale, and a stale
   `ownedScopeIds` is a read the caller is no longer entitled to. The `SCOPE_OWNER`/`SCOPE_USER` rows
   are authoritative. This also matches UC-07, whose `GetPersonByIdQueryHandler` resolves the same
   kind of rule from the database using only `ActingPersonId`/`ActingRole`.

3. **The whole rule is answered by the query that already loads the scope.** Ownership is a
   comparison against `ScopeOutput.OwnerIds`, which the projection already selects; membership is one
   projected `bool`. No new repository dependency, no second round trip, and — because the handler
   keeps a single collaborator — no mock is needed to unit-test the rule:

   ```csharp
   private static bool MayView(GetScopeByIdQuery query, ScopeProjection scope) => query.ActingRole switch
   {
       (int)Roles.SystemAdmin => true,
       (int)Roles.ScopeAdmin => scope.Output.OwnerIds.Contains(query.ActingPersonId),
       (int)Roles.User => scope.ActorBelongsToScope,
       _ => false
   };
   ```

   `IScopeOwnershipChecker` is deliberately **not** used. It exists for handlers that hold a scope id
   but not the scope's owners (`ListScopePersonsQueryHandler`, `CreateUserCommandHandler`); here the
   owner collection is already in hand, and injecting the checker would add a dependency whose
   built-in System Admin bypass would then have to be re-stated in every unit test's mock.

4. **A denied read is AF-02b: a new `ScopeMessages.NotAuthorizedToViewScope` mapped to `403`** in
   `ScopeMessageMap`, mirroring `PersonMessages.NotAuthorizedToViewPerson` for UC-07's AF-07b.

5. **AF-02a is decided before AF-02b.** A scope that does not exist (or is logically deleted and was
   not explicitly requested) is `404` regardless of who asks — the same order
   `GetPersonByIdQueryHandler` uses. The reverse order would need an authorization answer about a row
   that isn't there.

6. **An unrecognized role is denied** (the `_` arm). Default-deny keeps a future role from silently
   inheriting read access to every scope.

7. **`ApplyActor` moves out of `PersonController` into an extension** on `HttpContext`
   (`WebApi/Security/ActorExtensions.cs`), because `ScopeController` is now the second caller. The
   acting fields keep coming from `HttpContext.GetUser<IdentityUser>()` and never from the request.

8. **`includeDeleted` stays available to every actor** (FR-SC-07). It is unchanged from the merged
   behavior, and after this change a non-admin can only ever reach a scope they own or belong to, so
   the flag exposes nothing new — it lets an owner or member still read their scope after it is
   logically deleted.

## Consequences

- `GetScopeByIdQuery` gains `IActorScoped`; `ScopeController.GetById` populates it from the token.
- `GET /api/scopes/{id}` gains a third outcome: `403` with `NotAuthorizedToViewScope`.
- No schema change, no EF migration, no change to `ListScopesQuery(Handler)` or `ScopeOutput`.

## Testing

Per the Testing Specification: the handler rule is unit-tested (it lives in the handler, not in
middleware), and every endpoint outcome is functional-tested.

**Unit — `GetScopeByIdQueryHandlerTests` (existing four tests keep passing, with acting fields added):**

| Scenario | Expected |
| --- | --- |
| System Admin, any scope | scope returned |
| Scope Admin owning the scope | scope returned |
| Scope Admin not owning the scope | AF-02b `NotAuthorizedToViewScope` |
| User belonging to the scope | scope returned |
| User of another scope | AF-02b `NotAuthorizedToViewScope` |
| Unrecognized role | AF-02b `NotAuthorizedToViewScope` |
| Missing scope, non-admin actor | AF-02a `ScopeNotFound` (not-found wins) |

**Functional — `ScopeControllerViewTests`:**

| Scenario | Expected |
| --- | --- |
| Scope Admin owning the scope | `200` |
| Scope Admin not owning the scope | `403` |
| User of the scope | `200` |
| User of another scope | `403` |
| System Admin (existing test) | `200` |
