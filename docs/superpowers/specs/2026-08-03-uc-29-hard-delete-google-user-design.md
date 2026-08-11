# UC-29 — Hard Delete Google User — Design

**Issue:** [#30](https://github.com/artur-rios/heimdall-api/issues/30)
**Branch:** `feature/uc-29-hard-delete-google-user`
**Traces to:** FR-GO-16 (hard deletion, permanently removing the record).

---

## 1. What already exists

- `GoogleUser` and the `google_user` table (UC-25), with a cascading FK to `scope` (NFR-14).
- `GoogleUserMessages` / `GoogleUserMessageMap` and `GoogleUserController` (UC-27, extended by UC-28)
  — this use case adds one message and one action to files that already exist.

**No migration is needed.**

---

## 2. The simplest use case in the batch, and why

UC-29 has one actor, one alternative flow, and no cascade. It is
`HardDeleteApplicationCommandHandler` almost verbatim, and the reasons are documented rather than
assumed:

**No authorization rule in the handler.** UC-29's only actor is the System Admin, and the
`[RoleRequirement(SystemAdmin)]` on the endpoint settles that completely — there is nothing
data-dependent left, so the command carries no `IActorScoped` at all. UC-20 makes the same call. This
is the one Google User endpoint where the attribute *is* the whole rule.

**No self-deletion refusal.** UC-09 and UC-10 both refuse one, so its absence here deserves a
sentence: a Google User is always `User`-equivalent (FR-GO-04) and can never hold `SystemAdmin`, so
the only actor who may call this endpoint can never be its target. There is no way to lock yourself
out, and inventing AF-10c's guard here would be a flow no document defines.

**No cascade.** The Deletion Strategy section says it outright — *"Hard deleting a Google User simply
removes its record"* — because a Google User can own no application (FR-AP-03 restricts ownership to
a `ScopeAdmin` who owns the scope) and holds no password reset or email verification tokens. So
unlike `HardDeleteScopeCommandOutput` and `HardDeletePersonCommandOutput`, the output reports no
dependent totals: there are none to count.

### 2.1 One 404 for four situations

The lookup is qualified by the route's scope and omits any `!IsDeleted` filter. That collapses four
cases into AF-29a, correctly:

| Situation | Why 404 |
| --- | --- |
| Unknown Google User id | The resource does not exist |
| Unknown scope id | Nor does it |
| Google User living in another scope | Not the resource this path addresses |
| Called twice | The row is already gone — UC-29 defines no idempotent path, unlike UC-28's AF-28b |

Omitting the deletion filter is the deliberate part: a **logically deleted** Google User is exactly
what a cleanup pass starts from and must remain purgeable. UC-05, UC-10, and UC-20 all make the same
call.

---

## 3. Components

| Artifact | Role |
| --- | --- |
| `Command/Input/HardDeleteGoogleUserCommand.cs` | `{ ScopeId, Id }`, both from the route. **No** `IActorScoped` |
| `Command/Output/HardDeleteGoogleUserCommandOutput.cs` | `{ Id }` — no dependent totals, because there are no dependents |
| `Command/Handlers/HardDeleteGoogleUserCommandHandler.cs` | Deps: `IAsyncReadOnlyRepository<GoogleUser>`, `IAsyncRepository<GoogleUser>` |

**No validator**, as UC-28 registers none: both fields are typed route parameters.

### 3.1 Messages

| Message | Status | Flow |
| --- | --- | --- |
| `GoogleUserHardDeletedSuccessfully` (new) | 200 | Main flow |
| `GoogleUserNotFound` (UC-27's) | 404 | AF-29a |

### 3.2 Presentation layer

`DELETE /api/scopes/{scopeId}/google-users/{id}/hard` on the existing `GoogleUserController`, with
`[RoleRequirement(SystemAdmin)]` — matching System Requirements §5.5 and the authorization matrix,
which grants this to the System Admin alone and withholds it even from an owning Scope Admin.

---

## 4. Alternative flow coverage

| Flow | Condition | Answer |
| --- | --- | --- |
| Main | System Admin, Google User exists in the scope | 200, row gone |
| AF-29a | Unknown id, unknown scope, wrong scope, or already removed | 404 |
| — | Scope Admin, even an owner of the scope | 403 by `[RoleRequirement]` |
| — | Any `User`, Google or password | 403 by `[RoleRequirement]` |
| — | Anonymous | 401 |

The last three are not alternative flows UC-29 names — they are the framework's answers to an actor
the use case simply does not include — but they are tested, because "System Admin only" is a claim
worth proving.

---

## 5. What this use case does *not* do

- **No cascade, and no touch of the scope.** The FK points from the Google User to the scope, not the
  other way.
- **No idempotent repeat.** A second call is AF-29a; UC-28 is the use case with an idempotent path.
- **No logical-deletion precondition.** A Google User need not be soft-deleted first — UC-29 states
  no such precondition, and requiring one would invent a flow.
