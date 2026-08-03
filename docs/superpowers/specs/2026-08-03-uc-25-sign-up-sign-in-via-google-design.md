# UC-25 — Sign Up / Sign In via Google — Design

**Issue:** [#26](https://github.com/artur-rios/identity-manager-api/issues/26)
**Branch:** `feature/uc-25-sign-up-sign-in-via-google`
**Traces to:** FR-GO-03 … FR-GO-11 (plus FR-GO-12/FR-GO-13, which AF-25d and AF-25b enforce),
NFR-13, NFR-15.

---

## 1. What already exists

UC-25 lands on a schema that was built for it in the initial migration and extended by UC-24:

- `GoogleUser` entity (`Domain/Entities/GoogleUser.cs`) with `PublicId`, `GoogleId`, `Name`,
  `Email`, `EmailVerified`, `ProfilePictureUrl`, `IsDeleted`, `ScopeId`, timestamps.
- `google_user` table, with unique indexes on `(scope_id, google_id)` (FR-GO-08) and
  `(scope_id, email)` (FR-GO-07, Google-User half), and a cascading FK to `scope` (FR-GO-06, NFR-14).
- `Scope.GoogleSignInEnabled`, written by UC-24's `SetGoogleSignInCommandHandler`.
- `IAuthTokenIssuer` / `AuthTokenSubject` / `JwtAuthTokenIssuer` — UC-11's token issuance,
  reusable as-is.

Nothing in the data layer changes. **No migration is needed.**

---

## 2. Components

### 2.1 Application layer (`…Command`)

| Artifact | Role |
| --- | --- |
| `Input/GoogleSignInCommand.cs` | `{ Guid ScopeId; string IdToken; }`. Anonymous — no `IActorScoped`. |
| `Output/GoogleSignInCommandOutput.cs` | `{ string Token; DateTime ExpiresAt; }`, mirroring `LoginCommandOutput`. |
| `Services/IGoogleIdTokenVerifier.cs` | `Task<GoogleIdTokenPayload?> VerifyAsync(string idToken)` plus the `GoogleIdTokenPayload(string Subject, string Email, bool EmailVerified, string? Name, string? PictureUrl)` record. Returns `null` on any verification failure. |
| `Handlers/GoogleSignInCommandHandler.cs` | The use case. |

> **Why a project-owned verifier rather than the library's.** `ArturRios.Util.WebApi` already ships
> `IGoogleTokenVerifier` / `GoogleTokenVerifier` over `Google.Apis.Auth`, but its
> `GoogleTokenPayload` carries only `Subject`, `Email`, and `EmailVerified`. UC-25 step 6 and
> FR-GO-05 require `Name` and `ProfilePictureUrl` from the token's `name` and `picture` claims, so
> the library record cannot express what this use case must persist. The new interface lives in the
> application layer for the same reason `IAuthTokenIssuer` does: the handler states *what* it needs
> verified, the presentation layer owns *how*.

**No validator.** UC-25 defines no `400` flow, and it does not need one — an absent or empty
`IdToken` fails verification and lands on AF-25a (401), and `Guid.Empty` matches no scope and lands
on AF-25b (403). Both are the outcomes the specification already names, so adding a fourth status
code would be inventing a flow rather than satisfying NFR-10. (UC-21/22/23 also register no
validator.)

### 2.2 Handler flow

Constructor dependencies: `IGoogleIdTokenVerifier`, `IAsyncReadOnlyRepository<Scope>`,
`IAsyncReadOnlyRepository<Person>`, `IAsyncReadOnlyRepository<GoogleUser>`,
`IAsyncRepository<GoogleUser>`, `IAuthTokenIssuer`.

| Step | Action | Flow |
| --- | --- | --- |
| 1 | Verify the ID token. `null` → error. | AF-25a → 401 |
| 2 | Load the scope by `PublicId` where `!IsDeleted && GoogleSignInEnabled`. Missing → error. | AF-25b → 403 (FR-GO-03/13) |
| 3 | Find `GoogleUser` by `(ScopeId, GoogleId == payload.Subject)`. Deliberately no `IsDeleted` filter — AF-25d must find it to refuse it. | — |
| 4a | **Found and `IsDeleted`** → error. | AF-25d → 401 (FR-GO-12) |
| 4b | **Found and live** → refresh nothing; proceed to step 6. | FR-GO-10 |
| 5 | **Not found** → check the email is free in the scope, jointly across live `GOOGLE_USER` rows and `User` persons in `SCOPE_USER` (case-insensitive, as `CreateUserCommandHandler` does). Taken → error. Otherwise insert the row from the payload. | AF-25c → 409 (FR-GO-07); FR-GO-05/06/09 |
| 6 | Issue the token: `AuthTokenSubject(googleUser.PublicId, (int)Roles.User, scope.PublicId, [])`. | FR-GO-04, NFR-15 |
| 7 | Return `{ Token, ExpiresAt }` with `AuthMessages.GoogleSignInSuccessful`. | 200 |

Ordering note: step 1 runs before step 2 because the specification's sequence diagram verifies the
token first, and because an unverified caller should learn nothing about which scopes exist.

### 2.3 Messages (`…Shared/Messages/AuthMessages` + `AuthMessageMap`)

| Constant | Value | Status | Flow |
| --- | --- | --- | --- |
| `GoogleSignInSuccessful` | `"Google sign-in successful."` | 200 | main |
| `GoogleAuthenticationFailed` | `"Google authentication failed."` | 401 | AF-25a, AF-25d |
| `GoogleSignInUnavailable` | `"Google sign-in is not available for this scope."` | 403 | AF-25b |
| `EmailAlreadyExists` | `"A person with this email already exists."` | 409 | AF-25c |

AF-25a and AF-25d share one message for the reason UC-11's five rejections do: an anonymous caller
must not be able to use the endpoint to discover which Google accounts are registered in a scope or
which have been deleted.

`EmailAlreadyExists` repeats the literal value of `PersonMessages.EmailAlreadyExists` — the same
fact, answered by a different use case. That is already precedented by
`AuthMessages.PersonNotFound`, and costs nothing because the two message maps are separate
dictionaries.

### 2.4 Presentation layer (`…WebApi`)

| Artifact | Role |
| --- | --- |
| `Controllers/AuthController.Google` | `POST /api/auth/google`, `[AllowAnonymous]`, resolved through `AuthMessageMap.StatusCodes`. |
| `Security/GoogleSignInOptions.cs` | `FromEnvironment()` reading `IDENTITY_MANAGER_GOOGLE_CLIENT_IDS` (comma-separated audiences) and `IDENTITY_MANAGER_GOOGLE_TEST_SIGNING_SECRET`. |
| `Security/GoogleIdTokenVerifier.cs` | The real one. `GoogleJsonWebSignature.ValidateAsync` with `Audience = clientIds` — signature, issuer, audience, expiry (FR-GO-11, NFR-13). Any `InvalidJwtException` → `null`. |
| `Security/UnconfiguredGoogleIdTokenVerifier.cs` | Rejects every token, logging a warning once. Registered when no client IDs are configured. |
| `Security/LocalGoogleIdTokenVerifier.cs` | Validates an **HS256 JWT signed with a locally held secret** and reads the same five claims. Registered **only** outside Production and **only** when the test signing secret is set. |

`Startup.AddGoogleSignIn()` picks one of the three, mirroring `AddEmailSenders`.

> ### Design decision: how the functional suite exercises this endpoint
>
> `WebApiTest<T>` exposes neither the `WebApplicationFactory` (private) nor a settable `Gateway`
> (protected readonly), so a functional test **cannot** override a DI registration. Every existing
> substitution in this suite is therefore environment-driven at startup — that is exactly what
> `AddEmailSenders` does for Mailgun.
>
> UC-25's functional coverage requires reaching the main flow, AF-25c, and AF-25d, all of which sit
> *behind* token verification. Without a substitute, only AF-25a and AF-25b would be reachable, and
> §7.4 requires every `AF-xx`.
>
> `LocalGoogleIdTokenVerifier` is the substitute, and it is deliberately **not** a "trust anything"
> stub: it still requires a cryptographically valid signature, just under a secret the test fixture
> holds instead of Google's. Two guards keep it out of production: `Builder.Environment.IsProduction()`
> short-circuits it, and it is inert unless `IDENTITY_MANAGER_GOOGLE_TEST_SIGNING_SECRET` is set.
> The variable is absent from `.env.local` and is set only by `PostgresFixture`.
>
> **The alternative considered** was leaving the main flow functionally untested and covering it in
> unit tests only. Rejected: it would leave the `google_user` insert, the joint-email index, and the
> issued token's round trip unverified end-to-end, which is precisely what §7.2 exists for.

### 2.5 DI (`Startup`)

```csharp
Builder.Services.AddScoped<ICommandHandlerAsync<GoogleSignInCommand, GoogleSignInCommandOutput>,
    GoogleSignInCommandHandler>();   // no validator — see §2.1
AddGoogleSignIn();
```

---

## 3. Alternative-flow map

| Flow | Condition | Enforced by | Status |
| --- | --- | --- | --- |
| AF-25a | Token invalid / expired / unverifiable / absent | handler step 1 | 401 |
| AF-25b | Scope missing, deleted, or `GoogleSignInEnabled = false` | handler step 2 | 403 |
| AF-25c | Email taken by a live Google User or a `User` person in the scope | handler step 5 | 409 |
| AF-25d | Existing Google User is logically deleted | handler step 4a | 401 |

---

## 4. Out of scope

- Sign-out (UC-26), reading (UC-27), and deletion (UC-28/29) of Google Users.
- Distinguishing a Google User from a `Person` in an *incoming* bearer token. UC-25 issues the token
  the specification describes — subject `PublicId`, `role = User`, scope `PublicId`. UC-26 and UC-27
  are the first use cases that must tell the two apart on the way *in*, and the claim that does so
  belongs to whichever of them is implemented first. Flagged here so it is not mistaken for an
  oversight.
