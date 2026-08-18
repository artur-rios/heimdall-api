# Two-factor status is readable, and Scope Admins are listable for a picker

**Date:** 2026-08-18
**Status:** Approved

## Problem

Two gaps, both surfaced by the UI client, both of the same shape: state the API holds and
acts on, but never publishes.

**Two-factor status is write-only.** Every two-factor endpoint under `/api/auth/2fa` is a
`POST` — `enable`, `confirm`, `verify`, `disable`, `recovery-codes/regenerate`. The
`TwoFactorAuth` row carries `IsActive`, `AppEnabled`, and `EmailEnabled`, and
`TwoFactorRecoveryCode` carries `Used`, but nothing reads any of it back. `PersonOutput`
has no two-factor field either. A signed-in person therefore cannot be shown whether their
own account is protected, which methods are configured, whether a setup they started was
ever confirmed, or how many recovery codes they have left; and an administrator listing
persons cannot see which accounts are protected at all.

**Scope Admins cannot be discovered.** `GET /api/scopes/{scopeId}/owners` lists the owners
a scope already has, and `GET /api/persons/{id}` reads one person by identifier. There is
no listing of Scope Admins across scopes. UI-11 (create a scope) must select at least one
owner, and UI-14 step 3 adds an existing Scope Admin as a co-owner; with no listing behind
them, both degrade to typing a person identifier by hand.

The second gap also exposes a rule conflict. UC-07's per-actor visibility rule lets a Scope
Admin see "the Scope Admins co-owning those scopes" — that is, only admins they already
share a scope with. UI-14 step 3 has a Scope Admin adding a co-owner who by definition does
not own the scope yet, so under the current rule that person can never be found. UI-14's
AF-14b ("person is not a Scope Admin") and AF-14c ("already an owner") are written as
errors a Scope Admin can hit, so the specification already assumes the flow works for them.

## Scope

In scope:

1. `GET /api/auth/2fa` — the caller's own two-factor status.
2. `twoFactorEnabled` on `PersonOutput`.
3. `GET /api/persons/scope-admins` — a paginated, filterable Scope Admin listing with a
   minimal projection, optionally excluding a scope's current owners.
4. The requirements, use case, OpenAPI, HTTP/Bruno client, and documentation those imply.

Out of scope: a general `GET /api/persons` listing; any change to the existing
`POST /api/auth/2fa/*` endpoints; exposing another person's two-factor *methods* to an
administrator; widening UC-07's existing person-visibility rule beyond the Scope Admin
summary listing defined here.

## Design

### 1. `GET /api/auth/2fa`

Added to `AuthController`, which today injects only a `CommandMediator` and gains a
`QueryMediator` alongside it. Authenticated, with no `RoleRequirement` — the same reasoning
its `POST` siblings document: the authorization matrix grants two-factor management to
`User`, `ScopeAdmin`, and `SystemAdmin` alike and withholds it from anonymous callers,
which authentication alone enforces. The actor comes from `HttpContext.ApplyActor`, never
from a path identifier, preserving the `TwoFactorAuth` invariant that a person's
configuration is "never addressed by ID in a path" and is reached only through their own
authenticated identity.

New types in `ArturRios.Heimdall.Query`:

| Type | Contents |
| --- | --- |
| `GetTwoFactorStatusQuery : IActorScoped` | No public fields; the two `[JsonIgnore]` actor properties only |
| `TwoFactorStatusOutput : QueryOutput` | `IsActive`, `AppEnabled`, `EmailEnabled`, `RemainingRecoveryCodes` |
| `GetTwoFactorStatusQueryHandler` | Resolves the caller against `Person` (live, not deleted), then reads their `TwoFactorAuth` |

`RemainingRecoveryCodes` counts `TwoFactorRecoveryCode` rows where `Used` is `false`.

**No configuration at all answers `200` with everything false and zero, not `404`.** Never
having enabled two-factor authentication is the ordinary state of most accounts, and a
settings screen would otherwise have to render its most common state out of an error
branch. `404` stays reserved for the genuine refusals `TwoFactorMessages.NotActive`
already covers on UC-39 and UC-40.

**A Google User caller answers `403` with the existing `TwoFactorMessages.NotEligible`.** A
Google-issued token names a `GoogleUser`, not a `Person`, and FR-2F-01 makes Google Users
permanently ineligible. An all-false `200` would imply "off, and you could turn it on",
which is false. This is the same refusal, with the same message, that UC-36's AF-36b
already returns for the same caller — reused rather than duplicated.

One new message, `TwoFactorMessages.StatusRetrieved`
("Two-factor authentication status retrieved."), mapped to `200` in `TwoFactorMessageMap`.

**A pending setup needs no field of its own.** A row with `IsActive = false` and
`AppEnabled` or `EmailEnabled` set means UC-36 initiated setup and UC-37 has not confirmed
it. The client needs that state to offer "finish setting up", and it is exactly
`!isActive && (appEnabled || emailEnabled)` — derivable from the four fields, so no fifth
`setupPending` field is added. The derivation is documented on `TwoFactorStatusOutput` so
it is not rediscovered by each consumer.

### 2. `PersonOutput.TwoFactorEnabled`

A single `bool`, projected as `x.TwoFactorAuth != null && x.TwoFactorAuth.IsActive` — the
`Person.TwoFactorAuth` navigation already exists. The method breakdown is deliberately not
published here: an administrator learns *whether* an account is protected, which is what
makes coverage visible in a listing, but not *how*, which would otherwise hand any holder
of an administrator token a map of which accounts fall back to email.

Three projections change: `GetPersonByIdQueryHandler`, `ListScopePersonsQueryHandler`,
`ListScopeOwnersQueryHandler`.

### 3. `GET /api/persons/scope-admins`

Added to `PersonController`, whose route prefix is already `api`. The literal segment does
not collide with `persons/{id:guid}`, since the `:guid` constraint cannot match it.
`[RoleRequirement(SystemAdmin, ScopeAdmin)]`.

Query string: `pageNumber`, `pageSize`, `name`, `email`, `excludeOwnersOfScopeId`.

| Type | Contents |
| --- | --- |
| `ListScopeAdminsQuery : BaseQuery, IActorScoped` | `Name`, `Email`, `ExcludeOwnersOfScopeId` (`Guid?`) |
| `PersonSummaryOutput : QueryOutput` | `Id`, `Name`, `Email` |
| `ListScopeAdminsQueryHandler` | Filters, excludes, orders, paginates |
| `ListScopeAdminsQueryValidator : PaginatedQueryValidator<>` | Page bounds and filter lengths, matching its siblings |

**Who may call it, and why the projection is minimal.** Both administrator roles may call
it, because UI-14 step 3 is specified for both, and restricting the listing to System Admins
would silently narrow that documented flow — leaving a Scope Admin only the
create-a-new-co-owner and promote-a-user paths that already exist. What a Scope Admin learns from
it is that an administrator with a given name and address exists, which they can already
learn today by posting that address to `POST /api/scopes/{id}/owners` and reading the
duplicate-email rejection. So the audience widens without the disclosure widening, but only
because the projection is three fields.

`PersonSummaryOutput` is a new type rather than a reuse of `PersonOutput` for exactly that
reason. Reusing `PersonOutput` would ship `role`, `ownedScopeIds`, `emailVerified`, the
timestamps, and — after change 2 above — `twoFactorEnabled` to every Scope Admin. The
two-factor flag in particular must not travel sideways to an audience that was widened for
an unrelated purpose.

**No `IncludeDeleted`.** A logically deleted administrator is never a valid owner —
`PersonNotValidScopeAdmin` rejects them — so offering one in a picker can only produce a
failed submission. The handler always filters `!IsDeleted`.

**Filtering and ordering.** `RoleId == ScopeAdmin`, `!IsDeleted`, case-insensitive
`Contains` on name and email, then the exclusion below, ordered by `Name` with `Id` as
tiebreaker before pagination. The tiebreaker is not decorative: it is the same reasoning
`ListScopePersonsQueryHandler` documents — names are not unique, PostgreSQL gives no
ordering guarantee between tied sort keys, and each page is a separate query, so without it
two administrators sharing a name and straddling a page boundary could appear on both pages
while a third appeared on neither.

**The exclusion is applied before pagination.** `excludeOwnersOfScopeId` removes the named
scope's current owners from the candidate set, satisfying UI-14's AF-14c server-side.
Doing it client-side instead would be quietly wrong: both lists are paginated, so a
client-side diff is only correct once every page of owners has been fetched, and it also
makes page sizes ragged — a page of twenty candidates rendering as seventeen.

**The exclusion parameter is gated on ownership.** When `ExcludeOwnersOfScopeId` is set,
the handler runs the same `IScopeOwnershipChecker.ActorMayManageScopeAsync` check the
sibling list handlers use: an unknown or logically deleted scope answers `ScopeNotFound`, a
scope the actor does not own answers `NotScopeOwner`, and a System Admin bypasses. Without
this gate the endpoint leaks: calling it twice, once with the parameter and once without,
and diffing the two result sets enumerates the owners of *any* scope, including scopes the
caller has no relationship to — which is precisely the disclosure the minimal projection
was chosen to avoid. UI-14 only ever passes a scope the actor is already managing, so
nothing legitimate is blocked. The unfiltered call remains open to both administrator
roles.

All other messages are existing ones: `PersonsRetrievedSuccessfully`, `ScopeNotFound`,
`NotScopeOwner`.

### 4. Consumers

UI-11's owner selector calls the endpoint with no `excludeOwnersOfScopeId`, since no scope
exists yet. UI-14's "add existing Scope Admin" passes the open scope's identifier and
receives a correctly paginated list with current owners already removed.

## Requirements and documentation

**New functional requirements** in the System Requirements Document:

- **FR-PE-12** — listing Scope Admins system-wide, with pagination and optional
  case-insensitive name and email filters, and optional exclusion of a named scope's
  current owners; readable by a System Admin or a Scope Admin; returning identifier, name,
  and email only.
- **FR-2F-15** — reading the caller's own two-factor status: active state, configured
  methods, and unused recovery-code count.

**Amended:** FR-PE-04, to record `TwoFactorEnabled` on the person projection. The §12
traceability rows currently read "FR-PE-01 through FR-PE-11" and "FR-2F-01 through
FR-2F-14"; each extends by one.

**Use cases.** Both additions fold into existing use cases rather than becoming UC-41 and
UC-42. UC-07 gains a fourth read (list Scope Admins) and UC-36–UC-40 gain the status read.
The UI documents already trace UI-11 and UI-14 to UC-01 and UC-21–UC-23, and UI-09's
two-factor screens to UC-36–UC-40; minting new use case numbers would leave those traces
pointing at the wrong places. UC-07's per-actor visibility rule gains a sentence recording
the decision above — that a Scope Admin may list Scope Admins outside their own scopes, and
why the three-field projection makes that safe.

**Documentation and clients.** `docs/content/en/docs/api-reference.md` gains both
endpoints. `api-client/http/persons.http` and `auth.http`, and the matching Bruno folders,
gain requests. The OpenAPI document is regenerated with `scripts/openapi.py`;
`OpenApiContractTests` fails until that regeneration happens, which is expected rather than
a genuine break.

## Testing

**Unit — `GetTwoFactorStatusQueryHandlerTests`**

- No configuration row returns all false and zero.
- A pending configuration returns `isActive = false` with the selected methods set.
- An active configuration returns its methods.
- `RemainingRecoveryCodes` counts unused codes only.
- A Google User caller returns `NotEligible`.
- A caller naming no live person returns `NotEligible`.

**Unit — `ListScopeAdminsQueryHandlerTests`**

- Only `ScopeAdmin` persons are returned; Users and System Admins are not.
- Logically deleted administrators are excluded.
- Name and email filters match case-insensitively on a substring.
- `ExcludeOwnersOfScopeId` removes that scope's current owners.
- The exclusion is applied before pagination, so a full page is returned.
- An unknown or deleted scope returns `ScopeNotFound`.
- A Scope Admin who does not own the named scope returns `NotScopeOwner`.
- A System Admin bypasses the ownership check.
- Administrators sharing a name are ordered deterministically by the identifier tiebreaker.

**Functional**

- `AuthControllerGetTwoFactorStatusTests` — the states above over the real pipeline,
  including the `403` for a Google-issued token.
- `PersonControllerListScopeAdminsTests` — role requirement, filters, pagination, and the
  regression test for the leak in §3: a Scope Admin passing another scope's identifier
  receives `NotScopeOwner`.

**Amended:** `PersonControllerGetByIdTests` and the person list tests, for the new
`twoFactorEnabled` field in their expected payloads.
