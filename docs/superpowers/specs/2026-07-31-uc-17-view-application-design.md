# UC-17: View Application — Design

## Summary

Implement UC-17 (View Application, FR-AP-04 / FR-AP-05 / FR-AP-09): read one application by id and
list the applications of a scope.

This use case also carries a **specification correction**. Application ownership is restricted to
`ScopeAdmin` persons who own the application's scope; a `User` can neither own nor create an
application. The documents said otherwise, and UC-16 shipped against what they said, so the change
lands here: the requirement documents are rewritten, the UC-16 implementation is corrected to match,
and UC-17 is built on the corrected rule. See [Ownership correction](#ownership-correction).

| Method | Endpoint | Requirement | Handler | Guard |
| --- | --- | --- | --- | --- |
| GET | `/api/scopes/{scopeId}/applications/{id}` | FR-AP-04 | `GetApplicationByIdQueryHandler` | `[RoleRequirement(SystemAdmin, ScopeAdmin)]` |
| GET | `/api/scopes/{scopeId}/applications` | FR-AP-05 | `ListScopeApplicationsQueryHandler` | `[RoleRequirement(SystemAdmin, ScopeAdmin)]` |

Both exclude logically deleted applications unless `includeDeleted=true` is passed (FR-AP-09), and
both expose only `PublicId` identifiers — never the internal `bigint` foreign keys (SRD §4.0).

**No schema change / no EF migration.** The corrected rule is about *which person* may sit in
`application.owner_id`, not about the column: it is still a foreign key to `person.id`. Nothing in
`ApplicationDbMap` or the `InitialCreate` migration moves.

## Ownership correction

**The rule, as now specified:**

- An application is owned by exactly one `ScopeAdmin` person who owns the application's scope.
- A `User` may not own an application, and may not create one.
- A Scope Admin may read an application **they own** — by id, or in the scope listing.
- A System Admin may read every application in any scope.

### Why it lands in this use case

UC-17 cannot be built on the old rule and corrected afterwards: its whole authorization surface is
"who owns this application". Shipping it against the documents and then reversing it would mean
writing two sets of tests for the same endpoints. The correction is therefore made first, in the same
branch, and UC-17 is written once.

The alternative — a separate correction branch merged before UC-17 — was considered and rejected in
favour of one review: the suite is never left in a half-corrected state, and the UC-16 change is
small enough (one handler branch, one attribute, two message strings) to review alongside.

This departs from the workflow document's *one use case = one branch = one pull request* rule. The
departure is deliberate and is called out in the pull request body.

### Documents corrected

| Document | Change |
| --- | --- |
| SRD §2 glossary | "Application" — owned by a `ScopeAdmin` who owns its scope, not by any person |
| SRD FR-AP-03 | Owner must be an existing, non-logically-deleted `ScopeAdmin` who owns the application's scope |
| SRD §5.3 | `POST`/`GET`/`GET {id}` auth columns drop the `User` |
| SRD §7 matrix (diagram + table) | `Create`/`Read`/`Update`/`Delete Application` — `User` column becomes ❌; `ScopeAdmin` column becomes "✅ (owned)" |
| SRD §8 cascade notes | Hard-deleting a `User` no longer removes applications; hard-deleting a `ScopeAdmin` does. The Google User note drops its reference to `User` ownership |
| UC Spec actor diagram | The `User` actor no longer points at UC-16 … UC-19 |
| UC Spec UC-16 | Actors, preconditions, main flow step 4, sequence diagram, AF-16b, AF-16c |
| UC Spec UC-17 | Actors, main flow step 2 |
| UC Spec UC-18, UC-19 | Actors and the authorization steps that named the owning `User` |
| UC-16 design doc | "Superseded in part" banner; Decisions 1, 3, 4, 6 marked as reversed |

UC-20 (Hard Delete Application) is already System Admin only and needs no change.

### Code corrected (UC-16)

| File | Change |
| --- | --- |
| `ApplicationController.Create` | Gains `[RoleRequirement(SystemAdmin, ScopeAdmin)]`; XML doc rewritten |
| `CreateApplicationCommandHandler` | Actor branch and owner-eligibility query rewritten (below) |
| `ApplicationMessages.OwnerNotValidForScope` | Value becomes `"Owner must be a Scope Admin who owns the target scope."` |
| `CreateApplicationCommandHandlerTests` | The `User`-actor and `SCOPE_USER`-owner cases are replaced |
| `ApplicationControllerCreateTests` | Same, plus a new `User`-role 403 |

`HardDeletePersonCommandHandler` needs **no** change: it removes applications by
`OwnerId == person.Id` without consulting the owner's role, which is still correct — only the prose
describing it was wrong.

Corrected `CreateApplicationCommandHandler` flow:

| Step | Behavior | Flow |
| --- | --- | --- |
| 1 | Validate `Name` and `OwnerId` | AF-16d |
| 2 | Scope exists and `!IsDeleted` → else `Scope not found.` (404) | AF-16a |
| 3 | `IScopeOwnershipChecker` — System Admin bypasses, a Scope Admin must own the scope → else `NotScopeOwner` (403) | matrix |
| 4 | A Scope Admin actor must name themself as owner → else `CannotSetAnotherOwner` (403) | AF-16c |
| 5 | Owner is a non-deleted `ScopeAdmin` with a `SCOPE_OWNER` row on this scope → else `OwnerNotValidForScope` (400) | AF-16b, FR-AP-03 |
| 6 | Insert the row, return 201 | FR-AP-01/02 |

Steps 3 and 4 swap the old order: ownership of the scope is checked before who was named, so a Scope
Admin acting on a scope that is not theirs is told that, rather than being told about the owner. The
old AF-16c branch — "a `User` may only name themself" — becomes "a `ScopeAdmin` may only name
themself", keeping the alternative flow alive under the corrected rule.

## Shape (UC-17)

| Artifact | File | New/Edit |
| --- | --- | --- |
| `GetApplicationByIdQuery` | `…Query/Input/GetApplicationByIdQuery.cs` | new |
| `ListScopeApplicationsQuery` | `…Query/Input/ListScopeApplicationsQuery.cs` | new |
| `ApplicationOutput` | `…Query/Output/ApplicationOutput.cs` | new |
| `GetApplicationByIdQueryHandler` | `…Query/Handlers/GetApplicationByIdQueryHandler.cs` | new |
| `ListScopeApplicationsQueryHandler` | `…Query/Handlers/ListScopeApplicationsQueryHandler.cs` | new |
| `ApplicationMessages` / `ApplicationMessageMap` | `…Shared/Messages/` | edit |
| `ApplicationController` | `…WebApi/Controllers/ApplicationController.cs` | edit (two actions, `QueryMediator`) |
| DI | `…WebApi/Startup.cs` | edit (two registrations) |

Both queries derive from `BaseQuery` (page number/size inherited) and implement `IActorScoped`;
`ActingPersonId` / `ActingRole` are set by the controller from the bearer token and never bound from
the request, exactly as the UC-07 queries do.

## Decisions

1. **Both endpoints carry `[RoleRequirement(SystemAdmin, ScopeAdmin)]`.** Under the corrected rule a
   `User` can never own an application, so every `User` request to either endpoint is a refusal. The
   attribute states that at the framework layer instead of letting each handler rediscover it, and
   SRD §5.3's "Authenticated" cell for the by-id read is corrected to `ScopeAdmin+` to match.

   AF-17b stays observable on both endpoints without the `User` case: on by-id as a Scope Admin who
   does not own the application, on the listing as a Scope Admin who does not own the scope.

2. **By id, a Scope Admin sees an application only if they own it — not merely because they own the
   scope.** This is the instruction taken literally, and it is narrower than UC-17's original step 2.
   Its visible consequence: two co-owners of one scope cannot read each other's applications.

   The scope in the route is still a qualifier the lookup honours (Decision 3), so a Scope Admin
   cannot reach their own application through some other scope's path either.

3. **A by-id lookup is scoped by the route's `scopeId`, and every miss is one 404.** The query is
   `Application.PublicId == id && Application.Scope.PublicId == scopeId`. An unknown application, an
   application that exists in a *different* scope, and an unknown scope id all return AF-17a
   `Application not found.` (404). UC-17 defines exactly one 404 flow, and the addressed resource —
   *this* application under *this* scope — genuinely does not exist in all three cases.

   *Alternative rejected:* loading the scope first and answering `ScopeNotFound` for a bad
   `scopeId`, as the list endpoint does. On a listing the scope **is** the addressed collection; on a
   by-id read it is a path qualifier, and splitting the 404 in two would invent a flow UC-17 does not
   define.

4. **Existence is checked before authorization on by-id, giving AF-17a priority over AF-17b.**
   Literal reading of the specification, and it keeps both alternative flows observable — the same
   call UC-07 Decision 3 made. Ids are GUIDs, so the disclosure is that *some* application holds that
   GUID inside that scope, nothing more.

5. **On the listing, a Scope Admin must own the scope (403) and then sees only the applications they
   own.** The ownership gate is not redundant with the filter. Filtering alone would answer a Scope
   Admin probing a scope they have nothing to do with with an empty `200` — indistinguishable from a
   scope that is genuinely empty. `NotScopeOwner` (403) is the answer UC-06 AF-06e and UC-07 AF-07b
   already give for that fact, through the same `IScopeOwnershipChecker`.

   A System Admin bypasses both the gate (the checker returns `true` without a query) and the
   owner filter, and so sees every application in the scope.

6. **`IScopeOwnershipChecker` is reused rather than reimplemented.** It already encodes "System Admin
   bypasses; otherwise a non-deleted person with a `SCOPE_OWNER` row" — exactly the listing's gate.
   One implementation, one test class (`ScopeOwnershipCheckerTests`), as UC-06 and UC-07 already
   share.

7. **A logically deleted scope needs no special case on either endpoint.** UC-04's handler cascades
   `IsDeleted = true` from a scope to its applications, so an application in a deleted scope is
   itself deleted and FR-AP-09's default filter already hides it. The listing still refuses a deleted
   scope with `ScopeNotFound` (404) before reaching that filter, matching
   `ListScopePersonsQueryHandler`.

8. **The by-id owner branch does not re-check that the caller is still active.** A logically deleted
   Scope Admin holding a not-yet-expired token can still read an application they own, for exactly as
   long as UC-07 already lets them read their own person record. The listing is closed to them, since
   `IScopeOwnershipChecker` excludes a deleted actor. This is the existing project-wide trade-off
   (tokens are validated `ClaimsOnly`, with no per-request database read), not a new one, and closing
   it belongs with token revocation.

9. **The listing filters on name and owner, and pages by name.** FR-AP-05 requires "pagination and
   filtering" without naming the fields. `Name` is a case-insensitive substring match, as every name
   filter in this codebase is; `OwnerId` is an exact match on the owner's `PublicId` — useful to a
   System Admin narrowing a busy scope, and inert for a Scope Admin, who is already filtered to
   themselves. Ordering is by `Name`, as the scope and person listings order by theirs.

10. **`includeDeleted` is open to any caller the endpoint already admits,** exactly as UC-02 and
    UC-07 expose it. The per-actor rules still apply on top, so a Scope Admin passing
    `includeDeleted=true` still only ever sees applications they own.

11. **`ApplicationOutput` is one type shared by both handlers,** carrying `Id`, `Name`, `ScopeId`,
    `OwnerId`, `IsDeleted`, `CreatedAt`, `UpdatedAt` — with `ScopeId` / `OwnerId` being the scope's
    and owner's `PublicId`. `PersonOutput` and `ScopeOutput` are shared across their use case's
    endpoints the same way. It is deliberately **not** merged with
    `CreateApplicationCommandOutput`: that one is a command output without `IsDeleted` or
    `UpdatedAt`, and the two families sit in different projects.

12. **No system-wide `GET /api/applications`.** "A System Admin can list all applications" is
    satisfied by the scope-nested listing answering without an owner filter. No endpoint in SRD §5.3
    defines a global listing and FR-AP-05 scopes the requirement to "within a scope".

## Alternative flows → failure paths

| Flow | Endpoint | Condition | Path | Response |
| --- | --- | --- | --- | --- |
| AF-17a | by id | Unknown application, wrong scope, unknown scope, or deleted and not requested | lookup returns `null` | `404` `Application not found.` |
| AF-17b | by id | Scope Admin who does not own the application | owner comparison fails | `403` `You are not allowed to view this application.` |
| AF-17b | by id | Caller holds the `User` role | `[RoleRequirement]` (framework) | `403` |
| AF-17a | list | Scope missing or logically deleted | scope lookup returns `null` | `404` `Scope not found.` |
| AF-17b | list | Scope Admin does not own the scope | `IScopeOwnershipChecker` returns `false` | `403` `You are not an owner of the target scope.` |
| AF-17b | list | Caller holds the `User` role | `[RoleRequirement]` (framework) | `403` |
| (precondition) | both | Not authenticated | middleware | `401` |

## Messages and status map

Added to `ApplicationMessages` / `ApplicationMessageMap`:

| Message | Value | Status | Flow |
| --- | --- | --- | --- |
| `ApplicationRetrievedSuccessfully` | `"Application retrieved successfully."` | 200 | main flow (by id) |
| `ApplicationsRetrievedSuccessfully` | `"Applications retrieved successfully."` | 200 | main flow (list) |
| `ApplicationNotFound` | `"Application not found."` | 404 | AF-17a (by id) |
| `NotAuthorizedToViewApplication` | `"You are not allowed to view this application."` | 403 | AF-17b (by id) |

Reused from UC-16: `ScopeNotFound` (404) and `NotScopeOwner` (403) for the listing's two failures.
`OwnerNotValidForScope` keeps its key and status and changes only its text, per the correction above.

## Handlers

Both return failures as errors on the output and never throw, matching every handler before them.

### `GetApplicationByIdQueryHandler`

Deps: `IAsyncReadOnlyRepository<Application>`.

1. Project the application where `PublicId == query.Id`, `Scope.PublicId == query.ScopeId`, and
   `IncludeDeleted || !IsDeleted`, carrying the owner's `PublicId` alongside the `ApplicationOutput`
   (the private projection pattern `GetPersonByIdQueryHandler` uses, so the rule is decided without
   internal ids reaching the caller).
2. Miss → **AF-17a** `ApplicationNotFound` (404).
3. `ActingRole == SystemAdmin || OwnerPublicId == ActingPersonId` → else **AF-17b**
   `NotAuthorizedToViewApplication` (403).
4. Return with `ApplicationRetrievedSuccessfully`.

No `IScopeOwnershipChecker` here: under Decision 2 owning the scope is not by itself grounds to read
an application, so the only questions are "System Admin?" and "mine?".

### `ListScopeApplicationsQueryHandler`

Deps: `IAsyncReadOnlyRepository<Scope>`, `IAsyncReadOnlyRepository<Application>`,
`IScopeOwnershipChecker`.

1. Load the scope by `PublicId`, not deleted → else `ScopeNotFound` (404).
2. `ActorMayManageScopeAsync(actingRole, actingPersonId, scope.Id)` → else `NotScopeOwner` (403).
3. Applications where `ScopeId == scope.Id`; for a non-System-Admin actor, also
   `Owner.PublicId == ActingPersonId`; `!IsDeleted` unless requested; optional name and owner
   filters.
4. Project to `ApplicationOutput`, `PaginateAsync(pageNumber, pageSize, x => x.Name)`, message
   `ApplicationsRetrievedSuccessfully`.

## Endpoint wiring

Two actions added to the existing `ApplicationController` (its route already supplies `scopeId`):

- `[HttpGet("{id:guid}")] GetById(Guid scopeId, Guid id, [FromQuery] bool includeDeleted = false)`
- `[HttpGet] List(Guid scopeId, [FromQuery] ListScopeApplicationsQuery query)`

both `[RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]`. Each copies the route
`scopeId` and `HttpContext.ApplyActor(...)` onto the query, dispatches through `QueryMediator`, and
returns `ResponseResolver.Resolve(result, statusMap: ApplicationMessageMap.StatusCodes)`. The
controller gains a `QueryMediator` constructor parameter alongside its `CommandMediator`, as
`PersonController` has.

The list query is bound `[FromQuery]`, so a caller *can* put `actingPersonId` / `actingRole` in the
query string — `ApplyActor` runs after model binding and overwrites them unconditionally. A
functional test pins this, as UC-07's does.

DI in `Startup.AddDependencies`:

- `IQueryHandlerAsync<GetApplicationByIdQuery, ApplicationOutput>` → `GetApplicationByIdQueryHandler`
- `IPaginatedQueryHandlerAsync<ListScopeApplicationsQuery, ApplicationOutput>` →
  `ListScopeApplicationsQueryHandler`

## Test coverage

Per Testing Specification §6–§7: `FakeRepository<T>` for repositories, Moq for
`IScopeOwnershipChecker`, Bogus for entity data, GWT naming with `// Given / // When / // Then`.

**Unit — `GetApplicationByIdQueryHandlerTests`:** System Admin reads any application; the owning
Scope Admin reads their own; AF-17b for a Scope Admin who owns the *scope* but not the application
(Decision 2) and for an unrelated Scope Admin; AF-17a for an unknown id, for an application in a
different scope (Decision 3), for an unknown scope id, and for a deleted application with
`includeDeleted=false`; a deleted application **is** returned with `includeDeleted=true` (FR-AP-09);
and that the output carries public identifiers rather than internal ids.

**Unit — `ListScopeApplicationsQueryHandlerTests`:** main flow for a System Admin (every application
in the scope, paginated and ordered by name); main flow for an owning Scope Admin (only their own —
a co-owner's application is absent, Decision 2); `ScopeNotFound` for a missing scope and for a
logically deleted one; `NotScopeOwner` for a Scope Admin the checker rejects; deleted applications
excluded by default and included on request; the name and owner filters; and that another scope's
applications never appear.

**Unit — corrected `CreateApplicationCommandHandlerTests`:** System Admin naming an owning Scope
Admin; an owning Scope Admin naming themself; AF-16c for a Scope Admin naming a co-owner; 403 for a
Scope Admin who does not own the scope; AF-16b for an unknown owner, a logically deleted owner, an
owner who is a `User`, and an owner who is a Scope Admin of a *different* scope; AF-16a and AF-16d
unchanged.

**Functional — `ApplicationControllerGetByIdTests`:** System Admin → 200 with the application and no
internal ids in the payload; owning Scope Admin → 200; Scope Admin owning the scope but not the
application → 403; `User` role → 403; unknown id → 404; an application addressed through the wrong
scope → 404; deleted application → 404, and 200 with `includeDeleted=true`; no token → 401.

**Functional — `ApplicationControllerListTests`:** System Admin → 200 with every application in the
scope, including a co-owner's; owning Scope Admin → 200 with only their own; non-owning Scope Admin →
403; `User` role → 403; unknown scope → 404; logically deleted scope → 404; no token → 401; the name
filter, the owner filter, and pagination narrow the result; deleted applications excluded by default
and included on request; a forged `actingRole` in the query string is ignored.

**Functional — corrected `ApplicationControllerCreateTests`:** the `User`-actor and `SCOPE_USER`-owner
cases become a `User`-role 403 and a `User`-as-owner 400; the Scope Admin self-owner main flow
replaces the old User self-owner one.

## Not in scope

- **Updating or deleting applications.** UC-18 – UC-20; this use case adds no command handler beyond
  the UC-16 correction.
- **A system-wide application listing** (Decision 12).
- **Migrating existing `User`-owned application rows.** The corrected rule changes which owners are
  *accepted*; no deployment has run this schema with real data, so there is nothing to backfill. If
  that changes, it is a data migration, not a use case.
- **Applications as authenticating identities** (client credentials, secrets). Unchanged from UC-16.
- No schema change and no migration.
