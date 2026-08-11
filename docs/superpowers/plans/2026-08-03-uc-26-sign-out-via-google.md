# UC-26 — Sign Out via Google — Implementation Plan

Design: [2026-08-03-uc-26-sign-out-via-google-design.md](../specs/2026-08-03-uc-26-sign-out-via-google-design.md)
Issue: [#27](https://github.com/artur-rios/heimdall-api/issues/27) · Branch:
`feature/uc-26-sign-out-via-google` · Milestone: Google Sign-In

Sequenced test-first per the Testing Specification §9: each implementation step is followed by the
tests that pin it, and the suite is run before the pull request.

---

## Step 1 — Branch and mark started

- `git switch main && git pull && git switch -c feature/uc-26-sign-out-via-google`
- Move issue #27 to **In Progress**.

## Step 2 — Messages

- Add `GoogleSignOutSuccessful` to `AuthMessages`; map it to 200 in `AuthMessageMap.StatusCodes`.
- Extend `GoogleAuthenticationFailed`'s XML doc to name UC-26 AF-26a alongside AF-25a/AF-25d — the
  constant is reused, not duplicated (design §3.3).

## Step 3 — Application-layer contract

- `Command/Input/GoogleSignOutCommand.cs` — `BaseCommand`, `IActorScoped`, no fields of its own.
- `Command/Output/GoogleSignOutCommandOutput.cs` — empty `CommandOutput`.

No validator, and no new service interface: nothing is verified, issued, or persisted.

## Step 4 — Handler

- `Command/Handlers/GoogleSignOutCommandHandler.cs`, one dependency
  (`IAsyncReadOnlyRepository<GoogleUser>`), following design §3.2 and commented against the UC-26
  step and `AF-xx` each branch serves.
- The `!IsDeleted` filter goes **inside** the existence predicate, so AF-26a's two data conditions
  are indistinguishable in the answer.

## Step 5 — Unit tests (`Command.Tests/GoogleSignOutCommandHandlerTests.cs`)

`AsyncFakeRepository<GoogleUser>` for the repository, Bogus for the entity. GWT naming,
`// Given / // When / // Then` sections.

| Test | Flow |
| --- | --- |
| `GivenActiveGoogleUser_WhenHandlingGoogleSignOut_ThenSucceeds` | main flow, FR-GO-18 |
| `GivenNoMatchingGoogleUser_WhenHandlingGoogleSignOut_ThenAuthenticationFails` | AF-26a (hard deleted / password User / admin) |
| `GivenLogicallyDeletedGoogleUser_WhenHandlingGoogleSignOut_ThenAuthenticationFails` | AF-26a (UC-28) |
| `GivenSignOut_WhenHandlingGoogleSignOut_ThenLeavesTheGoogleUserUntouched` | design §5 — sign-out is not a deletion |

## Step 6 — Presentation layer

- `AuthController.GoogleSignOut` — `[HttpPost("google/sign-out")]`, no body, `ApplyActor`, resolved
  with `AuthMessageMap.StatusCodes`.
- **No `RoleRequirement`** (design §3.4); the XML doc records why, as `ResendVerification`'s does.
- Register the handler in `Startup.AddDependencies`, next to UC-25's, with the "no validator" note.

## Step 7 — Functional tests (`WebApi.Tests/AuthControllerGoogleSignOutTests.cs`)

`WebApiTest<Program>` + `PostgresFixture`, `TestTokens` for the caller's bearer token and
`TestGoogleTokens` for the round trip.

| Test | Flow |
| --- | --- |
| `GivenLiveGoogleUser_WhenPostGoogleSignOut_ThenSucceeds` | main flow |
| `GivenGoogleSignIn_WhenPostGoogleSignOutWithTheIssuedToken_ThenSucceeds` | UC-25 → UC-26 end to end, proving the precondition |
| `GivenSignedOutGoogleUser_WhenReadingTheRowBack_ThenItIsUnchanged` | nothing is written |
| `GivenAnonymousCaller_WhenPostGoogleSignOut_ThenUnauthorized` | AF-26a (middleware half) |
| `GivenLogicallyDeletedGoogleUser_WhenPostGoogleSignOut_ThenUnauthorized` | AF-26a |
| `GivenTokenNamingNoGoogleUser_WhenPostGoogleSignOut_ThenUnauthorized` | AF-26a |
| `GivenAdministrator_WhenPostGoogleSignOut_ThenUnauthorized` (theory: SystemAdmin, ScopeAdmin) | AF-26a, FR-GO-04 |

## Step 8 — Run the suite

`dotnet test src/ArturRios.Heimdall.sln` — both `Category=Unit` and `Category=Functional`
green before the pull request.

## Step 9 — Tracker and pull request

- Mark UC-26 done in the README backlog; record the new test classes and refreshed totals in the
  Testing Specification inventory.
- Open the PR into `main` with `Closes #27`.
