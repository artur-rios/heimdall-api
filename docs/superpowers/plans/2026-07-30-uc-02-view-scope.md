# UC-02: View Scope — Implementation Plan (completion)

Design: `docs/superpowers/specs/2026-07-30-uc-02-view-scope-design.md`
Issue: #3 · Branch: `feature/uc-02-view-scope` · Base: `main`

Sequenced per the project's Development Workflow §4: implement (Step 3) → **Gate 2** → Testing status
(Step 4) → run the suite (Step 5) → **Gate 3** → PR (Step 6).

Within that, each behavior is added **test-first**: the tests for the per-actor rule are written and
seen to fail (Phase 3) before the rule exists (Phase 4). Phases 1, 2 and 4 leave the suite green;
Phase 3 is the one deliberately red commit.

**Scope:** the per-actor rule on `GET /api/scopes/{id}` and AF-02b. Nothing else about UC-02 changes —
no schema change, no EF migration, no change to `GET /api/scopes`, `ListScopesQuery(Handler)`, or
`ScopeOutput`.

**Global constraints**

- Handlers return `DataOutput<T>` and report failures as errors carrying a canonical `ScopeMessages`
  value; `ResponseResolver` maps it to a status via `ScopeMessageMap.StatusCodes`. Never throw.
- Routes, inputs and outputs use `PublicId` (GUID); joins use internal `Id` (bigint). No internal id
  reaches a response, a route, or a token (NFR-15).
- Acting fields come from `HttpContext.GetUser<IdentityUser>()`, never from the request body or query
  string.
- Roles: `SystemAdmin = 1`, `ScopeAdmin = 2`, `User = 3`.
- Tests: `[UnitFact]`/`[FunctionalFact]`, GWT names, `// Given` / `// When` / `// Then` sections;
  unit tests use `AsyncFakeRepository<T>`, functional tests derive from `WebApiTest<Program>`, join
  `[Collection(nameof(FunctionalCollection))]`, authorize via `TestTokens`, and assert response and
  database state.
- Run filters: `dotnet test src/ArturRios.IdentityManager.sln --filter "Category=Unit"` and
  `--filter "Category=Functional"`.
- Commits: lowercase Conventional Commits subject ≤50 chars, imperative; body wrapped at 72; trailer
  `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.

---

## Phase 1 — Vocabulary the rule needs

Nothing behavioral; the suite stays green.

1.1 `src/Application/ArturRios.IdentityManager.Shared/Messages/ScopeMessages.cs` — add

```csharp
/// <summary>AF-02b: the caller is not allowed to view the requested scope.</summary>
public const string NotAuthorizedToViewScope = "You are not allowed to view this scope.";
```

1.2 `src/Application/ArturRios.IdentityManager.Shared/Messages/ScopeMessageMap.cs` — map it to
`HttpStatusCodes.Forbidden`, under an `// AF-02b — caller may not view this scope.` comment.

1.3 `src/Application/ArturRios.IdentityManager.Query/Input/GetScopeByIdQuery.cs` — implement
`IActorScoped` (`ArturRios.IdentityManager.Shared.Security`): `Guid ActingPersonId`, `int ActingRole`,
with the same "set by the controller from the authenticated caller, never from the request" doc note
`ListScopePersonsQuery` carries.

1.4 `dotnet build src/ArturRios.IdentityManager.sln` → succeeds. Commit.

## Phase 2 — Carry the acting caller to the query

Still no behavior change: the handler ignores the new fields until Phase 4, so the suite stays green.

2.1 Create `src/Presentation/ArturRios.IdentityManager.WebApi/Security/ActorExtensions.cs` — move
`PersonController.ApplyActor` here verbatim as an extension, keeping its doc comment (updated to name
UC-02 AF-02b alongside UC-06 AF-06e and UC-07 AF-07b):

```csharp
public static class ActorExtensions
{
    public static void ApplyActor(this HttpContext httpContext, IActorScoped actorScoped)
    {
        var actor = httpContext.GetUser<IdentityUser>()!;

        actorScoped.ActingPersonId = actor.Id;
        actorScoped.ActingRole = actor.RoleId;
    }
}
```

2.2 `PersonController.cs` — delete the private `ApplyActor` method and change its eight call sites to
`HttpContext.ApplyActor(command)` / `HttpContext.ApplyActor(query)`.

2.3 `ScopeController.GetById` — build the query into a local, apply the actor, then dispatch:

```csharp
var query = new GetScopeByIdQuery { Id = id, IncludeDeleted = includeDeleted };
HttpContext.ApplyActor(query);
```

Update the action's doc comment: the per-actor visibility rule (AF-02b) is data-dependent and is
enforced by the handler.

2.4 `dotnet build`, then the full suite → green. Commit.

## Phase 3 — Tests for the rule (expected red)

3.1 `tests/Application/ArturRios.IdentityManager.Query.Tests/GetScopeByIdQueryHandlerTests.cs` —
add acting fields to the four existing queries (`ActingRole = (int)Roles.SystemAdmin`,
`ActingPersonId = Guid.NewGuid()`), extend the `ScopeWithOwner` helper with an optional member so a
`User` scenario can be seeded, and add the seven scenarios from the design's testing table:

- `GivenSystemAdminActor_WhenHandlingGetById_ThenScopeIsReturned`
- `GivenScopeAdminOwningScope_WhenHandlingGetById_ThenScopeIsReturned`
- `GivenScopeAdminNotOwningScope_WhenHandlingGetById_ThenReturnsNotAuthorizedToViewScope`
- `GivenUserBelongingToScope_WhenHandlingGetById_ThenScopeIsReturned`
- `GivenUserOfAnotherScope_WhenHandlingGetById_ThenReturnsNotAuthorizedToViewScope`
- `GivenUnrecognizedRole_WhenHandlingGetById_ThenReturnsNotAuthorizedToViewScope`
- `GivenMissingScopeAndNonAdminActor_WhenHandlingGetById_ThenReturnsScopeNotFound`

3.2 `tests/Presentation/ArturRios.IdentityManager.WebApi.Tests/ScopeControllerViewTests.cs` — seed
helpers for a `ScopeAdmin` owner (`ScopeOwners` row) and a `User` member (`ScopeUsers` row), following
`PersonControllerGetByIdTests.SeedScopeAdminAsync`/`SeedUserAsync`, plus four tests:

- `GivenScopeAdminOwningScope_WhenGetScopeById_ThenReturnsScope` → `200`
- `GivenScopeAdminNotOwningScope_WhenGetScopeById_ThenForbidden` → `403`
- `GivenUserOfScope_WhenGetScopeById_ThenReturnsScope` → `200`
- `GivenUserOfAnotherScope_WhenGetScopeById_ThenForbidden` → `403`

Authorize with `TestTokens.For(person.PublicId, (int)Roles.ScopeAdmin)` /
`TestTokens.For(person.PublicId, (int)Roles.User)` — the rule reads the database, so the token only
has to name the right person and role.

3.3 Run the suite. **Expected: the three deny tests fail** (`403` expected, `200` returned — the
handler still returns any scope to any caller). Every other test passes. Record the failure output;
commit the tests.

## Phase 4 — The per-actor rule

4.1 `src/Application/ArturRios.IdentityManager.Query/Handlers/GetScopeByIdQueryHandler.cs` — project
into a private `ScopeProjection` (mirroring `GetPersonByIdQueryHandler.PersonProjection`) carrying the
existing `ScopeOutput` plus the one fact the rule needs:

```csharp
private sealed class ScopeProjection
{
    public bool ActorBelongsToScope { get; init; }

    public ScopeOutput Output { get; init; } = null!;
}
```

Select it with `ActorBelongsToScope = x.Users.Any(u => u.Person.PublicId == query.ActingPersonId)`,
keep the AF-02a not-found check first, then apply the rule and return
`output.WithError(ScopeMessages.NotAuthorizedToViewScope)` when it denies:

```csharp
// UC-02 step 2: a System Admin sees every scope, a Scope Admin only the scopes they own, a User
// only the scope they belong to. Any other role is denied.
private static bool MayView(GetScopeByIdQuery query, ScopeProjection scope) => query.ActingRole switch
{
    (int)Roles.SystemAdmin => true,
    (int)Roles.ScopeAdmin => scope.Output.OwnerIds.Contains(query.ActingPersonId),
    (int)Roles.User => scope.ActorBelongsToScope,
    _ => false
};
```

Update the class doc comment to record AF-02b next to AF-02a.

4.2 Run `--filter "Category=Unit"` → green.

4.3 Run `--filter "Category=Functional"` → green.

4.4 Run the whole suite once more and commit.

## Phase 5 — Documentation

5.1 `README.md` — flip UC-02 to ✅ in the Scope Management table and delete the "UC-02 is not
finished" note below the Platform table.

5.2 Commit this plan and its design document under `docs/superpowers/`.

The Use Case Specification already documents the rule and AF-02b as designed, and the System
Requirements Document already lists both endpoints with the auth levels being implemented — neither
needs a change.

---

## **Gate 2** — implementation complete

Summarize what was built, which flows are covered, and any deviation from this plan. On approval,
confirm issue #3's board status (it is already **Testing** from the earlier partial work, and the
workflow's statuses only move forward, so no transition is expected).

---

## **Gate 3** — suite green, before the pull request

Report the real `dotnet test` output for both categories. On approval, push and open a PR into `main`
with `Closes #3`.

---

## Definition of Done (Development Workflow §5)

- [ ] Branch `feature/uc-02-view-scope` from `main`
- [ ] Main flow (all three actors) and AF-02a / AF-02b implemented
- [ ] Unit tests cover `GetScopeByIdQueryHandler` for every actor and both alternative flows
- [ ] Functional tests cover both endpoints, including the authorization flows
- [ ] `Category=Unit` and `Category=Functional` both pass
- [ ] PR reviewed by a human and merged to `main`
- [ ] Feature branch deleted
- [ ] Issue #3 in **Done** and closed
