# UC-26 — Sign Out via Google — Design

**Issue:** [#27](https://github.com/artur-rios/heimdall-api/issues/27)
**Branch:** `feature/uc-26-sign-out-via-google`
**Traces to:** FR-GO-18. AF-26a leans on FR-GO-04 (a Google User is always `User`-equivalent) and on
FR-GO-12's stance that a logically deleted Google User is not authenticated.

---

## 1. What already exists

UC-26 adds no schema and no infrastructure. Everything it needs was built by UC-11 and UC-25:

- `GoogleUser` entity and the `google_user` table (UC-25) — read-only here.
- `IAuthTokenIssuer` / `JwtAuthTokenIssuer` — UC-11's stateless token issuance, **not** used by this
  use case, but it is what defines the token strategy UC-26 defers to.
- `HttpContext.ApplyActor` and `IActorScoped` — how the controller hands the authenticated caller to
  a handler.
- `AuthMessages.GoogleAuthenticationFailed`, already mapped to 401 by UC-25.

**No migration is needed.** Nothing is written at all.

---

## 2. The one decision this use case turns on

UC-26 step 2 says the system "invalidates the token (e.g., via a revocation list) **or** instructs
the client to discard it, **per the configured token strategy**." Two readings, and the clause that
settles it is the third one: which strategy is configured.

This project's is the stateless one UC-11 established and its design recorded in as many words — no
refresh tokens, no logout, no token revocation. NFR-03 asks only that tokens be signed and expire.
So:

> **UC-26 does not revoke. It verifies the caller still holds a live Google session and answers the
> success that tells the client to drop the token.**

Building a denylist instead would be choosing a token strategy no document chooses, and it would
have to apply to every token the API issues — UC-11's included — which is a change to authentication
as a whole, not the delivery of one Google use case. If revocation is wanted later, it is its own
piece of work and this endpoint is where it would attach.

### 2.1 What that leaves the endpoint doing

The lookup, and the lookup is not ceremony. Authentication runs in `ClaimsOnly` mode — no database
read per request — so a valid bearer token **outlives** the Google User it names once UC-28
logically deletes them or UC-29 removes them outright. Both use cases ship in this same batch. The
same fact is why UC-15 answers `PersonNotFound` for a token that outlived a hard deletion.

UC-26's precondition is "a valid authentication token issued via UC-25". A token naming a Google
User that UC-25 itself would now refuse to authenticate (AF-25d) no longer satisfies it, so it is
AF-26a.

---

## 3. Components

### 3.1 Application layer (`…Command`)

| Artifact | Role |
| --- | --- |
| `Input/GoogleSignOutCommand.cs` | `IActorScoped` only — no fields of its own. |
| `Output/GoogleSignOutCommandOutput.cs` | Empty `CommandOutput`, as `ResendVerificationEmailCommandOutput` is. |
| `Handlers/GoogleSignOutCommandHandler.cs` | The use case. One dependency: `IAsyncReadOnlyRepository<GoogleUser>`. |

**The empty command is the authorization rule.** A Google User id in the body would be a way to sign
somebody else out; UC-26 describes a Google User ending *their own* session. Same reasoning, and the
same shape, as `ResendVerificationEmailCommand`.

**No validator**, for UC-15's reason: there is no caller-supplied input to validate (NFR-10).

### 3.2 Handler flow

| Step | Action | Flow |
| --- | --- | --- |
| 1 | `AnyAsync(x => x.PublicId == ActingPersonId && !x.IsDeleted)` over `google_user`. False → error. | AF-26a → 401 |
| 2–3 | Nothing to revoke; return success. | Main flow → 200 |

The `!IsDeleted` filter is folded into the same predicate rather than split into a second branch, so
a Google User that is missing and one that is logically deleted are refused **alike** — the endpoint
cannot be used to tell them apart.

### 3.3 Messages

| Message | Status | Flow |
| --- | --- | --- |
| `GoogleSignOutSuccessful` (new) | 200 | Main flow |
| `GoogleAuthenticationFailed` (UC-25's, reused) | 401 | AF-26a |

Reused rather than duplicated: `AuthMessageMap` is keyed by the message string, so a second constant
holding a different wording for the same fact would only make the two endpoints distinguishable to
an attacker for no gain.

### 3.4 Presentation layer

`POST /api/auth/google/sign-out` on `AuthController`, matching the System Requirements §5.5 table.
No request body — the action binds nothing and reads the caller from the token.

**No `RoleRequirement`, deliberately.** The authorization matrix grants Google sign-out to a Google
User acting on themselves and marks it *not applicable* for both administrator roles, who can never
be Google Users (FR-GO-04). The real rule is therefore "the caller is a live Google User" — data the
attribute cannot see. Leaving the attribute off also keeps the endpoint to exactly the two answers
UC-26 defines: 200, or 401 for every rejection. An attribute would introduce a 403 the use case
never mentions. This mirrors `resend-verification`, which omits it for the same kind of reason.

The missing-token half of AF-26a never reaches the handler — authentication answers it with the same
401.

---

## 4. Alternative flow coverage

| Flow | Condition | Where enforced | Answer |
| --- | --- | --- | --- |
| Main | Caller's token names a live Google User | Handler step 1 | 200 `GoogleSignOutSuccessful` |
| AF-26a | Token missing or malformed | Authentication middleware | 401 |
| AF-26a | Token names a logically deleted Google User (UC-28) | Handler step 1 | 401 `GoogleAuthenticationFailed` |
| AF-26a | Token names no Google User — hard deleted (UC-29), or a password `User` | Handler step 1 | 401 `GoogleAuthenticationFailed` |
| AF-26a | Caller is a `ScopeAdmin` or `SystemAdmin` | Handler step 1 (their id names no Google User) | 401 `GoogleAuthenticationFailed` |

---

## 5. What this use case does *not* do

- **No revocation store, and no change to authentication.** See §2.
- **No cascade, no write, no timestamp touch.** Signing out is not a deletion; a test pins that the
  row is unchanged afterwards.
- **No new configuration.** Nothing here reads the Google client ids — the ID token was already
  exchanged for an application token by UC-25.
