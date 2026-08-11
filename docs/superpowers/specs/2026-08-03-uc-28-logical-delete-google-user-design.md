# UC-28 — Logical Delete Google User — Design

**Issue:** [#29](https://github.com/artur-rios/heimdall-api/issues/29)
**Branch:** `feature/uc-28-logical-delete-google-user`
**Traces to:** FR-GO-15 (logical deletion by setting `IsDeleted = true`), FR-GO-17 (deleted records
excluded from default query results — already delivered by UC-27, and this is what makes it visible).

---

## 1. What already exists

- `GoogleUser.IsDeleted` and the `google_user` table (UC-25).
- `GoogleUserMessages` / `GoogleUserMessageMap` and `GoogleUserController` (UC-27) — this use case
  adds a message pair and a `DELETE` action to files that already exist.
- `IScopeOwnershipChecker` — the "System Admin, or an owner of this scope" rule, which is exactly
  UC-28 step 2.
- `UC-26`'s handler already refuses a logically deleted Google User, so the consequence of this use
  case is already implemented and tested on the sign-out side.

**No migration is needed.** The column exists.

---

## 2. The shape, and the one thing it is not

This is `DeleteApplicationCommandHandler` with a different authorization rule. The structure —
lookup that deliberately omits `!IsDeleted`, authorization, idempotent short-circuit, flag, stamp —
is UC-09's and UC-19's, and departing from it here would be novelty for its own sake.

The difference is step 2. UC-19 compares the *owner* of the application, because owning the scope is
not grounds to delete another owner's application. UC-28 has no such notion: a Google User has no
owner, only a scope (FR-GO-06), and the use case says "System Admin, or an owner of the Google
User's scope" in as many words. So it consults `IScopeOwnershipChecker`, as UC-09's Scope Admin
branch does.

### 2.1 Flow ordering, and why it is not arbitrary

| Order | Flow | Why here |
| --- | --- | --- |
| 1 | AF-28a — not found | The lookup omits `!IsDeleted`, so an already-deleted record is **found**, not 404'd — otherwise AF-28b could never fire |
| 2 | AF-28c — not authorized | Before AF-28b, so an already-deleted Google User cannot be used to probe for records outside the caller's reach |
| 3 | AF-28b — already deleted | Idempotent success, nothing written |
| 4 | Main — flip and stamp | |

Step 2 before step 3 is the same ordering `DeletePersonCommandHandler` and
`DeleteApplicationCommandHandler` use, and for the same reason. Reversed, a caller could distinguish
"exists but you may not touch it" from "already deleted" and learn about a scope they do not own.

### 2.2 `UpdatedAt` on the idempotent path

Left alone. The row already carries the requested state, and re-stamping would misreport *when* the
deletion happened. Again UC-19's decision, recorded here because the temptation to "touch it anyway"
recurs.

### 2.3 No cascade

A Google User owns nothing. FR-AP-03 restricts application ownership to a `ScopeAdmin` who owns the
scope, and a Google User is always `User`-equivalent (FR-GO-04), so it can own no application; it has
no password reset or email verification tokens, because authentication is delegated to Google. The
Deletion Strategy section says as much for the hard delete, and the logical delete cascades even less.

### 2.4 What a logical deletion actually costs the account

Nothing here enforces it, but it is worth recording that the effect is already complete: UC-25
refuses to authenticate a logically deleted Google User (AF-25d, FR-GO-12), UC-26 refuses their
sign-out (AF-26a), and UC-27 hides them from default reads (FR-GO-17). This use case only sets the
flag those three already honour.

---

## 3. Components

| Artifact | Role |
| --- | --- |
| `Command/Input/DeleteGoogleUserCommand.cs` | `{ ScopeId, Id }` + `IActorScoped`, both bound from the route |
| `Command/Output/DeleteGoogleUserCommandOutput.cs` | `{ Id, AlreadyDeleted }` — the flag is what tells AF-28b from the main flow |
| `Command/Handlers/DeleteGoogleUserCommandHandler.cs` | Deps: `IAsyncReadOnlyRepository<GoogleUser>`, `IAsyncRepository<GoogleUser>`, `IScopeOwnershipChecker` |

**No validator.** Both fields come from typed route parameters, so there is no caller-supplied input
NFR-10 could reject that the route would not have refused first. UC-19 registers none either.

### 3.1 Messages — added to UC-27's files

| Message | Status | Flow |
| --- | --- | --- |
| `GoogleUserDeletedSuccessfully` (new) | 200 | Main **and** AF-28b |
| `NotAuthorizedToDeleteGoogleUser` (new) | 403 | AF-28c |
| `GoogleUserNotFound` (UC-27's) | 404 | AF-28a |

AF-28b shares the main flow's message and status because the specification requires it to; the
response's `AlreadyDeleted` flag is what distinguishes them.

### 3.2 Presentation layer

`DELETE /api/scopes/{scopeId}/google-users/{id}` on the existing `GoogleUserController`, which
gains a `CommandMediator`. `[RoleRequirement(SystemAdmin, ScopeAdmin)]` — the matrix withholds this
from every `User`, Google or not, so unlike UC-27's by-id read there is no actor the attribute would
wrongly exclude.

---

## 4. Alternative flow coverage

| Flow | Condition | Answer |
| --- | --- | --- |
| Main | Authorized, active Google User | 200, `AlreadyDeleted = false` |
| AF-28a | No such Google User in the addressed scope | 404 |
| AF-28b | Already logically deleted | 200, `AlreadyDeleted = true`, nothing written |
| AF-28c | Scope Admin who does not own the scope | 403 |
| AF-28c | Any `User`, Google or password | 403 by `[RoleRequirement]` |

---

## 5. What this use case does *not* do

- **No cascade** (§2.3), and no touch of the scope or any other row.
- **No hard delete.** That is UC-29, next.
- **No un-delete.** No document defines one; `IncludeDeleted` on UC-27's reads is how a deleted
  record is still inspected.
