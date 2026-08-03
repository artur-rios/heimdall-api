# UC-27 — View Google User — Design

**Issue:** [#28](https://github.com/artur-rios/identity-manager-api/issues/28)
**Branch:** `feature/uc-27-view-google-user`
**Traces to:** FR-GO-14 (read by id, list within a scope with pagination and filtering), FR-GO-17
(logically deleted excluded from default results). NFR-15 governs what may appear in the payload.

---

## 1. What already exists

- `GoogleUser` entity and the `google_user` table (UC-25) — read-only here, no schema change.
- `IScopeOwnershipChecker` — the shared "System Admin, or an owner of this scope" rule, used by
  UC-06 AF-06e and UC-07 AF-07b.
- `IPaginatedQueryHandlerAsync` / `PaginateAsync` — UC-02's listing machinery.
- `HttpContext.ApplyActor` / `IActorScoped`.

**No migration is needed.**

---

## 2. Two read shapes, one use case

UC-27 covers both endpoints in System Requirements §5.5, and they are not symmetric:

| | Get by id | List in scope |
| --- | --- | --- |
| Route | `GET /api/scopes/{scopeId}/google-users/{id}` | `GET /api/scopes/{scopeId}/google-users` |
| Documented auth | Authenticated | ScopeAdmin (owner)+ |
| Because | a Google User may read **their own** record | the matrix grants no Google User a listing |

That asymmetry is the whole of the authorization design. A `RoleRequirement` on the by-id endpoint
would lock out the very actor UC-27 names third, so it carries none; the listing carries
`[RoleRequirement(SystemAdmin, ScopeAdmin)]` because no `User` may ever call it.

### 2.1 The by-id rule (AF-27b)

UC-27 step 2 gives three grants. Two of them are exactly what `IScopeOwnershipChecker` already
decides — a System Admin sees any Google User, a Scope Admin sees those of the scopes they own — so
the rule reduces to:

```
caller is the Google User themselves  ||  ActorMayManageScopeAsync(role, callerId, scopeInternalId)
```

A password `User` fails both halves and is refused, which is correct: the matrix grants "Read Google
User" to a `User` only as *self*, and a password person is never a Google User's self.

Reusing the checker rather than restating ownership also inherits its guard that a **logically
deleted** actor owns nothing — a Scope Admin's token must not keep reading a scope's Google Users
after UC-09 deleted them.

### 2.2 Scope qualification, and why AF-27a comes first

Both routes are nested under `{scopeId}`. The by-id lookup therefore filters on the scope as well as
the id: a Google User that exists in *another* scope is not the resource this path addresses, so it
falls out as AF-27a rather than reaching the rule. This is `GetApplicationByIdQueryHandler`'s
arrangement, and it keeps the two alternative flows observable — a GUID nobody holds cannot be told
apart from one the caller may not see.

### 2.3 FR-GO-17

`IncludeDeleted` (default `false`) on both queries, exactly as `IncludeDeleted` works for persons
(FR-PE-08) and applications (FR-AP-09). Left off, a logically deleted Google User is absent from a
listing and is AF-27a on a by-id read.

---

## 3. Components

### 3.1 Application layer (`…Query`)

| Artifact | Role |
| --- | --- |
| `Output/GoogleUserOutput.cs` | `{ Id, GoogleId, Name, Email, EmailVerified, ProfilePictureUrl, IsDeleted, ScopeId, CreatedAt, UpdatedAt }` |
| `Input/GetGoogleUserByIdQuery.cs` | `{ ScopeId, Id, IncludeDeleted }` + `IActorScoped` |
| `Input/ListScopeGoogleUsersQuery.cs` | `{ ScopeId, Name?, Email?, IncludeDeleted }` + `IActorScoped`, paginated |
| `Handlers/GetGoogleUserByIdQueryHandler.cs` | Deps: `IAsyncReadOnlyRepository<GoogleUser>`, `IScopeOwnershipChecker` |
| `Handlers/ListScopeGoogleUsersQueryHandler.cs` | Deps: `IAsyncReadOnlyRepository<Scope>`, `IAsyncReadOnlyRepository<GoogleUser>`, `IScopeOwnershipChecker` |

**The payload is FR-GO-05's field list**, minus the internal `Id` that NFR-15 keeps out and plus the
timestamps every other output carries. `GoogleId` is included deliberately: it is the account's
externally meaningful identifier, the one FR-GO-08 makes unique per scope, and an administrator
correlating an account with Google has nothing else to correlate on. It is not a secret — it is
Google's public `sub`, and a caller who can read the record already knows the person.

`Name` and `Email` filters mirror `ListScopePersonsQuery`'s, satisfying FR-GO-14's "filtering"
with the vocabulary the suite's other listings already use.

### 3.2 Handler flows

**Get by id**

| Step | Action | Flow |
| --- | --- | --- |
| 1 | Project the Google User where `PublicId == Id && Scope.PublicId == ScopeId && (IncludeDeleted \|\| !IsDeleted)`. Miss → error. | AF-27a → 404 |
| 2 | Self, or `ActorMayManageScopeAsync`. Neither → error. | AF-27b → 403 |
| 3 | Return the projection. | Main → 200 |

**List in scope** — the shape of `ListScopePersonsQueryHandler`:

| Step | Action | Flow |
| --- | --- | --- |
| 1 | Scope by `PublicId` where `!IsDeleted`. Miss → error. | AF-27a → 404 |
| 2 | `ActorMayManageScopeAsync`. False → error. | AF-27b → 403 |
| 3 | Filter by `IncludeDeleted`, `Name`, `Email`; paginate by `Name`. | Main → 200 (FR-GO-14/17) |

### 3.3 Messages — a new pair of files

`GoogleUserMessages` / `GoogleUserMessageMap`, alongside the scope, person, application, and auth
pairs. Google Users are their own entity with their own endpoints, and UC-28 and UC-29 land in the
same files immediately afterwards.

| Message | Status | Flow |
| --- | --- | --- |
| `GoogleUserRetrievedSuccessfully` | 200 | Main (by id) |
| `GoogleUsersRetrievedSuccessfully` | 200 | Main (list) |
| `GoogleUserNotFound` | 404 | AF-27a |
| `ScopeNotFound` | 404 | AF-27a, listing's own miss |
| `NotAuthorizedToViewGoogleUser` | 403 | AF-27b (by id) |
| `NotScopeOwner` | 403 | AF-27b (list) |

The by-id and listing refusals are named separately, as UC-17's are: they are refusals of different
resources, and the listing's message says which scope the caller failed to own.

### 3.4 Presentation layer

New `GoogleUserController`, `[Route("api/scopes/{scopeId:guid}/google-users")]`, matching
`ApplicationController`'s shape. UC-28 and UC-29 add their `DELETE` actions to the same controller.

---

## 4. Alternative flow coverage

| Flow | Condition | Answer |
| --- | --- | --- |
| Main | Authorized read, by id or listed | 200 |
| AF-27a | No such Google User in this scope | 404 `GoogleUserNotFound` |
| AF-27a | Logically deleted and not explicitly requested (FR-GO-17) | 404 `GoogleUserNotFound` |
| AF-27a | Scope missing or logically deleted (listing) | 404 `ScopeNotFound` |
| AF-27b | Scope Admin who does not own the scope | 403 |
| AF-27b | Password `User`, or a Google User reading somebody else | 403 |
| AF-27b | A `User` calling the listing | 403 by `[RoleRequirement]` |

---

## 5. What this use case does *not* do

- **No write of any kind**, and no timestamp touch.
- **No cross-scope listing.** FR-GO-14 scopes the listing to one scope, and the route says so.
- **No token or credential material in the payload.** There is none to leak — a Google User has no
  `PasswordHash` or `Salt` (FR-GO-05).
