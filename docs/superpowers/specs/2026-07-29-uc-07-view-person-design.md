# UC-07: View Person — Design

## Summary

Implement UC-07 (View Person, FR-PE-03 / FR-PE-04 / FR-PE-08): read a person by id, list the
`User` persons of a scope, and list the `ScopeAdmin` owners of a scope. Three read endpoints, three
query handlers, one shared output type.

| Endpoint | Requirement | Handler |
| --- | --- | --- |
| `GET /api/persons/{id}` | FR-PE-03 | `GetPersonByIdQueryHandler` |
| `GET /api/scopes/{scopeId}/persons` | FR-PE-04 | `ListScopePersonsQueryHandler` |
| `GET /api/scopes/{scopeId}/owners` | FR-PE-04 | `ListScopeOwnersQueryHandler` |

Every path excludes logically deleted persons unless `includeDeleted=true` is passed (FR-PE-08), and
never exposes `PasswordHash` / `Salt` or any internal `bigint` id (NFR-15).

**No schema change / no EF migration:** `person`, `scope_user`, and `scope_owner` and their maps
already exist from `InitialCreate`.

## Decisions (from brainstorming)

1. **All three endpoints ship in this use case.** FR-PE-04 covers listing a scope's Users *and* its
   owners, and traces only to UC-07; `GET /api/scopes/{id}/owners` is not claimed by any other use
   case (UC-21 / UC-22 add and remove owners, they do not list them). UC-07's main-flow prose
   mentions only the Users listing, but the requirement it traces to is the authority.
2. **`IScopeOwnershipChecker` moves to `Shared`.** The two list endpoints need exactly the rule it
   already implements, and the `Query` project does not — and should not — reference `Command`.
   One implementation, one registration, consumed by both sides.
3. **On `GET /api/persons/{id}`, existence is checked before authorization.** A person that does not
   exist (or is deleted and not requested) is AF-07a → 404; a person that exists but is not visible
   to the caller is AF-07b → 403. Literal reading of the specification, and it keeps both
   alternative flows observable. Ids are GUIDs, so the disclosure is that *some* person holds that
   GUID, nothing more.
4. **`includeDeleted` is open to any authorized caller,** exactly as UC-02 exposes it on
   `GetScopeByIdQuery` / `ListScopesQuery`. The per-actor visibility rules still apply on top, so a
   `User` passing `includeDeleted=true` still only ever sees their own record.
5. **A missing or logically deleted target scope on a list endpoint is 404 `ScopeNotFound`,** read as
   AF-07a applied to the addressed resource. This matches UC-06's AF-06b on the same
   `/api/scopes/{scopeId}/…` routes and keeps a mistyped scope id distinguishable from a real but
   empty scope.

## Routing — existing `PersonController`

All three actions go on `PersonController`, which already carries `[Route("api")]` and the
scope-nested person routes from UC-06. `ScopeController` is not touched, even for the owners
listing: the resource returned is a person.

- `[HttpGet("persons/{id:guid}")]` — no `[RoleRequirement]`; any authenticated actor, per the
  endpoint table ("Authenticated"). The per-actor rule is a data-dependent decision and therefore
  lives in the handler, not in an attribute.
- `[HttpGet("scopes/{scopeId:guid}/persons")]` — `[RoleRequirement(SystemAdmin, ScopeAdmin)]`.
- `[HttpGet("scopes/{scopeId:guid}/owners")]` — `[RoleRequirement(SystemAdmin, ScopeAdmin)]`.

Each action is thin: bind the query, copy the route `scopeId` and the acting user onto it, dispatch
through `QueryMediator`, and return
`ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes)`.

## Authorization

### `GET /api/persons/{id}` — in-handler visibility rule

The target is loaded first (honouring `includeDeleted`); a miss is AF-07a → 404. The caller may then
see it when **any** of these holds — otherwise AF-07b → 403:

| Rule | Covers |
| --- | --- |
| Actor's role is `SystemAdmin` | "System Admin: can view any person" |
| Actor **is** the target person | "User: can view only their own record" (and a Scope Admin viewing themselves) |
| Actor is `ScopeAdmin`, target is a `User`, and the target's `SCOPE_USER` scope is one the actor owns | "Scope Admin: can view the Users of the scopes they own" |
| Actor is `ScopeAdmin`, target is a `ScopeAdmin`, and target and actor co-own a scope | "…and other Scope Admins co-owning those scopes" |

A `ScopeAdmin` therefore cannot see a `SystemAdmin`, an unrelated `ScopeAdmin`, or a `User` of a
scope they do not own; a `User` sees only themselves.

The rule has exactly one consumer, so it stays a private method on the handler and is covered
through the handler's unit tests rather than being extracted behind its own interface.

### List endpoints — scope ownership

1. Load the scope by `PublicId`, not logically deleted → else 404 `ScopeNotFound`.
2. `IScopeOwnershipChecker.ActorMayManageScopeAsync(actingRole, actingPersonId, scope.Id)` → else
   403 `NotScopeOwner` (AF-07b). A `SystemAdmin` bypasses.
3. Page the results.

`[RoleRequirement]` already rejects a plain `User` at the framework layer (403), so the handler only
has to separate owners from non-owning Scope Admins.

## Queries and output

Query inputs derive from `BaseQuery` (page number/size inherited) and implement `IActorScoped`.

| Type | Members |
| --- | --- |
| `GetPersonByIdQuery` | `Id` (Guid), `IncludeDeleted` (bool); `ActingPersonId`, `ActingRole` |
| `ListScopePersonsQuery` | `ScopeId` (Guid, route), `Name?`, `Email?`, `IncludeDeleted`; `ActingPersonId`, `ActingRole` |
| `ListScopeOwnersQuery` | `ScopeId` (Guid, route), `Name?`, `Email?`, `IncludeDeleted`; `ActingPersonId`, `ActingRole` |

`ActingPersonId` / `ActingRole` are set by the controller from the authenticated user, never bound
from the request. The two list queries are bound `[FromQuery]`, so a caller *can* put those names in
the query string — `ApplyActor` runs after model binding and overwrites them unconditionally, so a
forged value is discarded. A functional test pins this.

`PersonOutput : QueryOutput` is shared by all three:

| Field | Notes |
| --- | --- |
| `Id` (Guid) | `Person.PublicId` |
| `Name`, `Email` | |
| `Role` (int) | The `Roles` value; matches `CreatePersonCommandOutput.Role` |
| `EmailVerified`, `IsDeleted` | |
| `ScopeId` (Guid?) | The `SCOPE_USER` scope's `PublicId`; `null` for admins |
| `OwnedScopeIds` (IEnumerable&lt;Guid&gt;) | The `SCOPE_OWNER` scopes' `PublicId`s; empty for non-owners |
| `CreatedAt`, `UpdatedAt` | |

`PasswordHash` and `Salt` have no field to land in, so they cannot leak through projection.

Name and email filters are case-insensitive substring matches (`ToLower().Contains(...)`), consistent
with the case-insensitive email comparison UC-06's handlers already use. Results are ordered by
`Name`, as `ListScopesQueryHandler` orders by its own name column.

## Handlers

All three return failures as errors on the output and never throw, matching UC-01 – UC-06.

### `GetPersonByIdQueryHandler`
Deps: `IAsyncReadOnlyRepository<Person>`.
1. Load the person by `PublicId` where `IncludeDeleted || !IsDeleted`, projecting the fields the
   output needs plus the internal ids the visibility rule needs (own `Id`, `RoleId`, membership
   scope id, owned scope ids).
2. Miss → **AF-07a** `PersonNotFound` (404).
3. Apply the visibility rule above → fail → **AF-07b** `NotAuthorizedToViewPerson` (403).
4. Return the person with `PersonRetrievedSuccessfully`.

### `ListScopePersonsQueryHandler`
Deps: `IAsyncReadOnlyRepository<Scope>`, `IAsyncReadOnlyRepository<Person>`, `IScopeOwnershipChecker`.
1. Load the scope by `PublicId`, not deleted → else `ScopeNotFound` (404).
2. Ownership check → else `NotScopeOwner` (403).
3. Persons where `ScopeMembership.ScopeId == scope.Id`, `!IsDeleted` unless requested, plus the
   optional name/email filters.
4. Project to `PersonOutput`, `PaginateAsync(pageNumber, pageSize, x => x.Name)`, message
   `PersonsRetrievedSuccessfully`.

### `ListScopeOwnersQueryHandler`
Same as above, except step 3 selects persons where
`ScopeOwnerships.Any(o => o.ScopeId == scope.Id)`.

The two list handlers differ only in that predicate, but keeping them separate keeps each one
readable end-to-end and matches the one-query-one-handler shape the mediator registration expects.

## Messages and status map

Added to `PersonMessages` / `PersonMessageMap`:

| Message | Status | Flow |
| --- | --- | --- |
| `PersonRetrievedSuccessfully` | 200 OK | main flow (by id) |
| `PersonsRetrievedSuccessfully` | 200 OK | main flow (both lists) |
| `PersonNotFound` | 404 Not Found | AF-07a |
| `NotAuthorizedToViewPerson` | 403 Forbidden | AF-07b (by id) |

Reused unchanged: `ScopeNotFound` (404) for a missing target scope, `NotScopeOwner` (403) for AF-07b
on the list endpoints.

## Structural changes carried by this use case

Both are required by the work, not opportunistic refactoring.

### 1. `IScopeOwnershipChecker` moves to `Shared`

`Command/Services/IScopeOwnershipChecker.cs` and `ScopeOwnershipChecker.cs` move to
`Shared/Services/`. `ArturRios.IdentityManager.Shared.csproj` gains a project reference to `Domain`
and a package reference to `ArturRios.Data.Relational.Core`. `CreateUserCommandHandler` and
`CreateScopeOwnerCommandHandler` change their `using`; the DI registration in `Startup` changes
namespace only. Behaviour is unchanged.

Consequence: per Testing Specification §4 (one test project per production project), the existing
`ScopeOwnershipCheckerTests` moves out of `Command.Tests` into a new
`tests/Application/ArturRios.IdentityManager.Shared.Tests` project, registered in the solution under
the `Tests/Application` folder with the standard package set.

### 2. `IActorScopedCommand` becomes `IActorScoped`

The interface is not command-specific — the three UC-07 queries need the same two members. It moves
to `Shared/Security/IActorScoped.cs` under the neutral name; `CreateUserCommand` and
`CreateScopeOwnerCommand` implement it directly, and `PersonController.ApplyActor` takes an
`IActorScoped` so one method serves commands and queries. Name and namespace change only.

## Dependency injection (`Startup.AddDependencies`)

Register the three query handlers alongside the existing scope ones:

- `IQueryHandlerAsync<GetPersonByIdQuery, PersonOutput>` → `GetPersonByIdQueryHandler`
- `IPaginatedQueryHandlerAsync<ListScopePersonsQuery, PersonOutput>` → `ListScopePersonsQueryHandler`
- `IPaginatedQueryHandlerAsync<ListScopeOwnersQuery, PersonOutput>` → `ListScopeOwnersQueryHandler`

`IScopeOwnershipChecker` keeps its existing registration with an updated namespace.

## Components

| Layer | File | New/Edit |
| --- | --- | --- |
| Query / Input | `Query/Input/GetPersonByIdQuery.cs`, `ListScopePersonsQuery.cs`, `ListScopeOwnersQuery.cs` | new |
| Query / Output | `Query/Output/PersonOutput.cs` | new |
| Query / Handlers | `Query/Handlers/GetPersonByIdQueryHandler.cs`, `ListScopePersonsQueryHandler.cs`, `ListScopeOwnersQueryHandler.cs` | new |
| Shared / Security | `Shared/Security/IActorScoped.cs` | new (replaces `Command/Input/IActorScopedCommand.cs`) |
| Shared / Services | `Shared/Services/IScopeOwnershipChecker.cs`, `ScopeOwnershipChecker.cs` | moved from `Command/Services` |
| Shared / Messages | `Shared/Messages/PersonMessages.cs`, `PersonMessageMap.cs` | edit |
| Shared | `ArturRios.IdentityManager.Shared.csproj` | edit (Domain ref, Data.Relational.Core package) |
| Command | `Command/Input/CreateUserCommand.cs`, `CreateScopeOwnerCommand.cs`, `Command/Handlers/CreateUserCommandHandler.cs`, `CreateScopeOwnerCommandHandler.cs` | edit (usings / interface) |
| Presentation | `WebApi/Controllers/PersonController.cs` | edit (three actions, `ApplyActor` signature) |
| DI | `WebApi/Startup.cs` | edit |
| Solution | `src/ArturRios.IdentityManager.sln` | edit (new Shared.Tests project) |

## Testing (Testing Specification §6–§7)

Unit tests use `AsyncFakeRepository<T>`, Moq for `IScopeOwnershipChecker`, Bogus for entity data,
GWT naming and `// Given / // When / // Then` sections.

**Unit — `Query.Tests`:**

- `GetPersonByIdQueryHandlerTests`: SystemAdmin sees any person; a person sees themselves; a
  ScopeAdmin sees a `User` of an owned scope; a ScopeAdmin sees a co-owning ScopeAdmin; AF-07a
  (unknown id); AF-07a (deleted with `includeDeleted=false`); deleted with `includeDeleted=true`
  returns the record; AF-07b (a `User` requesting another person); AF-07b (a ScopeAdmin requesting a
  `User` of a scope they do not own); AF-07b (a ScopeAdmin requesting a SystemAdmin).
- `ListScopePersonsQueryHandlerTests`: main flow (the scope's Users, paginated); scope missing →
  `ScopeNotFound`; scope logically deleted → `ScopeNotFound`; non-owner → `NotScopeOwner`;
  SystemAdmin bypasses ownership; deleted persons excluded by default and included on request; name
  and email filters; the scope's owners do **not** appear in the users list.
- `ListScopeOwnersQueryHandlerTests`: the mirror set, asserting the scope's Users do **not** appear
  in the owners list.

**Functional — `WebApi.Tests`** (Testcontainers PostgreSQL, asserting response and seeded DB state,
one class per endpoint as UC-06 does):

- `PersonControllerGetByIdTests`: SystemAdmin → 200 with the person and no hash/salt in the payload;
  a `User` fetching themselves → 200; a `User` fetching another → 403; an owning ScopeAdmin fetching
  a scope `User` → 200; a non-owning ScopeAdmin → 403; unknown id → 404; deleted person → 404, and
  200 with `includeDeleted=true`; no token → 401.
- `PersonControllerListScopePersonsTests`: SystemAdmin → 200 with the scope's Users; owning
  ScopeAdmin → 200; non-owning ScopeAdmin → 403; unknown scope → 404; `User` role → 403; no token →
  401; name filter and pagination narrow the result.
- `PersonControllerListScopeOwnersTests`: the equivalent set over `SCOPE_OWNER`.

**Moved:** `ScopeOwnershipCheckerTests` → `tests/Application/ArturRios.IdentityManager.Shared.Tests`,
unchanged apart from its namespace.

## Out of scope / non-goals

- No person update or deletion (UC-08 – UC-10); no owner add/remove/promote (UC-21 – UC-23).
- No Google User listing (UC-27).
- No login (UC-11); functional tests authenticate with `TestTokens`, as prior use cases do.
- No schema change and no migration.
