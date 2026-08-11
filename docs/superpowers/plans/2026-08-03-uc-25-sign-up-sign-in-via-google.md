# UC-25 — Sign Up / Sign In via Google — Implementation Plan

Design: [2026-08-03-uc-25-sign-up-sign-in-via-google-design.md](../specs/2026-08-03-uc-25-sign-up-sign-in-via-google-design.md)
Issue: [#26](https://github.com/artur-rios/heimdall-api/issues/26) · Branch:
`feature/uc-25-sign-up-sign-in-via-google` · Milestone: Google Sign-In

Sequenced test-first per the Testing Specification §9: each implementation step is followed by the
tests that pin it, and the suite is run before the pull request.

---

## Step 1 — Branch and mark started

- `git switch main && git pull && git switch -c feature/uc-25-sign-up-sign-in-via-google`
- Move issue #26 to **In Progress**.

## Step 2 — Messages

- Add `GoogleSignInSuccessful`, `GoogleAuthenticationFailed`, `GoogleSignInUnavailable`, and
  `EmailAlreadyExists` to `AuthMessages`, each with the XML doc the file's style expects (naming the
  flow and the reason two flows share a message).
- Map them in `AuthMessageMap.StatusCodes` → 200 / 401 / 403 / 409.

## Step 3 — Application-layer contract

- `Command/Services/IGoogleIdTokenVerifier.cs`: the `GoogleIdTokenPayload` record and the interface.
- `Command/Input/GoogleSignInCommand.cs`, `Command/Output/GoogleSignInCommandOutput.cs`.

## Step 4 — Handler

- `Command/Handlers/GoogleSignInCommandHandler.cs`, following the seven steps in the design §2.2,
  commented against the UC-25 step and `AF-xx` each branch serves (the file style used by
  `SetGoogleSignInCommandHandler`).
- Joint email check mirrors `CreateUserCommandHandler`'s case-insensitive `ToLower()` comparison so
  it translates to `LOWER()` in SQL and matches how uniqueness is enforced elsewhere.

## Step 5 — Unit tests (`Command.Tests/GoogleSignInCommandHandlerTests.cs`)

`FakeRepository<T>`/`AsyncFakeRepository<T>` for repositories, Moq for the verifier and the token
issuer, Bogus for entities. GWT naming, `// Given / // When / // Then` sections.

| Test | Flow |
| --- | --- |
| `GivenNoExistingGoogleUser_WhenHandlingGoogleSignIn_ThenCreatesGoogleUserFromTokenClaimsAndIssuesToken` | main (sign-up), FR-GO-05/09 |
| `GivenExistingGoogleUser_WhenHandlingGoogleSignIn_ThenIssuesTokenWithoutCreatingDuplicate` | main (sign-in), FR-GO-10 |
| `GivenSignUp_WhenHandlingGoogleSignIn_ThenIssuedTokenClaimsUserRoleAndScope` | FR-GO-04, step 6 |
| `GivenTokenFailsVerification_WhenHandlingGoogleSignIn_ThenReturnsAuthenticationFailedError` | AF-25a |
| `GivenScopeDoesNotExist_WhenHandlingGoogleSignIn_ThenReturnsSignInUnavailableError` | AF-25b |
| `GivenScopeIsLogicallyDeleted_WhenHandlingGoogleSignIn_ThenReturnsSignInUnavailableError` | AF-25b |
| `GivenScopeHasGoogleSignInDisabled_WhenHandlingGoogleSignIn_ThenReturnsSignInUnavailableError` | AF-25b |
| `GivenEmailBelongsToAnotherGoogleUserInScope_WhenHandlingGoogleSignIn_ThenReturnsEmailAlreadyExistsError` | AF-25c |
| `GivenEmailBelongsToUserPersonInScope_WhenHandlingGoogleSignIn_ThenReturnsEmailAlreadyExistsError` | AF-25c |
| `GivenSameEmailInAnotherScope_WhenHandlingGoogleSignIn_ThenCreatesGoogleUser` | FR-GO-07 is per-scope |
| `GivenExistingGoogleUserIsLogicallyDeleted_WhenHandlingGoogleSignIn_ThenReturnsAuthenticationFailedError` | AF-25d |
| `GivenSameGoogleIdInAnotherScope_WhenHandlingGoogleSignIn_ThenCreatesSeparateGoogleUser` | FR-GO-06/08 |

## Step 6 — Presentation layer

- `WebApi/Security/GoogleSignInOptions.cs` — `FromEnvironment()`, plus the variable-name constants
  so the tests and the warning log share one source.
- `WebApi/Security/GoogleIdTokenVerifier.cs` — `GoogleJsonWebSignature.ValidateAsync` with the
  configured audiences; `InvalidJwtException` → `null`.
- `WebApi/Security/UnconfiguredGoogleIdTokenVerifier.cs` — always `null`.
- `WebApi/Security/LocalGoogleIdTokenVerifier.cs` — HS256 validation against the test signing
  secret, reading `sub`/`email`/`email_verified`/`name`/`picture`.
- `AuthController.GoogleSignIn` — `[HttpPost("google")] [AllowAnonymous]`.
- `Startup.AddGoogleSignIn()` + the handler registration; add `Google.Apis.Auth` as an explicit
  `PackageReference` on the WebApi project (currently only transitive) so the dependency is declared
  where it is used.
- Add the two new variables, commented and unset, to `Environments/.env.local`.

## Step 7 — Functional tests (`WebApi.Tests/AuthControllerGoogleSignInTests.cs`)

`PostgresFixture` sets `HEIMDALL_GOOGLE_TEST_SIGNING_SECRET` so the host boots with
`LocalGoogleIdTokenVerifier`. A `TestTokens`-style helper mints the signed ID tokens.

| Test | Flow |
| --- | --- |
| `GivenGoogleSignInEnabledAndUnknownGoogleAccount_WhenPostAuthGoogle_ThenGoogleUserIsCreatedAndTokenReturned` | main (sign-up) + DB state |
| `GivenExistingGoogleUser_WhenPostAuthGoogle_ThenTokenIsReturnedAndNoDuplicateIsCreated` | main (sign-in) + DB state |
| `GivenIssuedToken_WhenCallingAuthenticatedEndpoint_ThenTokenIsAccepted` | round trip, as UC-11's suite does |
| `GivenInvalidIdToken_WhenPostAuthGoogle_ThenReturnsUnauthorized` | AF-25a |
| `GivenMissingIdToken_WhenPostAuthGoogle_ThenReturnsUnauthorized` | AF-25a |
| `GivenUnknownScope_WhenPostAuthGoogle_ThenReturnsForbidden` | AF-25b |
| `GivenLogicallyDeletedScope_WhenPostAuthGoogle_ThenReturnsForbidden` | AF-25b |
| `GivenGoogleSignInDisabled_WhenPostAuthGoogle_ThenReturnsForbidden` | AF-25b |
| `GivenEmailAlreadyUsedByUserPersonInScope_WhenPostAuthGoogle_ThenReturnsConflict` | AF-25c |
| `GivenEmailAlreadyUsedByAnotherGoogleUserInScope_WhenPostAuthGoogle_ThenReturnsConflict` | AF-25c |
| `GivenLogicallyDeletedGoogleUser_WhenPostAuthGoogle_ThenReturnsUnauthorized` | AF-25d |

## Step 8 — Run until green

- `dotnet test --filter "Category=Unit"`
- `dotnet test --filter "Category=Functional"`
- Fix and re-run until both pass; report the real output.

## Step 9 — Documentation

- Testing Specification §10: add `GoogleSignIn` to the Command.Tests row and
  `AuthControllerGoogleSignIn` to the functional row; refresh the suite totals; note what the UC-25
  pair contributes.
- README tracker: UC-25 → ✅.
- README/Operations: document the two new environment variables.

## Step 10 — Pull request

Push and open a PR into `main` with `Closes #26`. Review and merge stay with the human.

---

## Risks

- **`Google.Apis.Auth` version.** Pinned transitively at `1.75.0` by `ArturRios.Util.WebApi 3.0.0`;
  the explicit reference will match that version, and the Technology Stack Document gains a row for
  it rather than a new floating dependency.
- **The local verifier.** Guarded twice (non-Production *and* an explicitly set secret). If either
  guard is judged insufficient at review, the fallback is to drop it and accept unit-only coverage of
  the three flows behind verification — noted in the design as the rejected alternative.
