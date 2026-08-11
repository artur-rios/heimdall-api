# UC-14: Verify Email — Implementation Plan

Design: [2026-07-30-uc-14-verify-email-design.md](../specs/2026-07-30-uc-14-verify-email-design.md)
Issue: [#15](https://github.com/artur-rios/heimdall-api/issues/15)
Branch: `feature/uc-14-verify-email`

Sequenced test-first per the
[Testing Specification Document](../../requirements/Testing%20Specification%20Document.md) §9.

## Step 0 — Rename the shared token messages (Decision 1)

- `AuthMessages`: `ResetTokenInvalid` → `TokenInvalid`, `ResetTokenExpired` → `TokenExpired`,
  `ResetTokenAlreadyUsed` → `TokenAlreadyUsed`. String values unchanged; XML docs updated to name
  AF-13a…c **and** AF-14a…c.
- Update the references in `AuthMessageMap`, `ResetPasswordCommandHandler`,
  `ResetPasswordCommandHandlerTests`, `AuthControllerPasswordResetTests`.
- Verify: `dotnet build` clean.

## Step 1 — Command, output, validator

- `VerifyEmailCommand : BaseCommand` — one property, `Token`.
- `VerifyEmailCommandOutput : CommandOutput` — empty (Decision 7).
- `VerifyEmailCommandValidator` — `Token` `NotEmpty` → `AuthMessages.TokenRequired` (Decision 3).

## Step 2 — Validator tests (red → green)

`tests/Application/…Command.Tests/VerifyEmailCommandValidatorTests.cs`:

- `GivenTokenIsPresent_WhenValidatingVerifyEmail_ThenValidationPasses`
- `GivenTokenIsMissing_WhenValidatingVerifyEmail_ThenTokenRequiredIsReported` (`[UnitTheory]`: `""`,
  `"   "`)

## Step 3 — Handler tests (red)

`tests/Application/…Command.Tests/VerifyEmailCommandHandlerTests.cs`, mirroring
`ResetPasswordCommandHandlerTests`: `AsyncFakeRepository<Person>` / `<EmailVerificationToken>`, a Moq
`IValidator<VerifyEmailCommand>`, `Person` navigation set explicitly (the fake resolves no `Include`).

| Test | Covers |
| --- | --- |
| `GivenLiveToken_WhenHandlingVerifyEmail_ThenEmailIsVerified` | main flow, FR-EV-03 |
| `GivenLiveToken_WhenHandlingVerifyEmail_ThenTokenIsConsumed` | UC-14 step 4 |
| `GivenExpiredToken_WhenHandlingVerifyEmail_ThenTokenExpiredIsReported` | AF-14a |
| `GivenExpiredToken_WhenHandlingVerifyEmail_ThenEmailStaysUnverified` | AF-14a side effect |
| `GivenUsedToken_WhenHandlingVerifyEmail_ThenTokenAlreadyUsedIsReported` | AF-14b |
| `GivenUnknownToken_WhenHandlingVerifyEmail_ThenTokenInvalidIsReported` | AF-14c |
| `GivenUnknownToken_WhenHandlingVerifyEmail_ThenEmailStaysUnverified` | AF-14c side effect |
| `GivenInvalidInput_WhenHandlingVerifyEmail_ThenValidationErrorsAreReturned` | NFR-10 |
| `GivenTwoLiveTokens_WhenHandlingVerifyEmail_ThenBothAreConsumed` | Decision 2 |
| `GivenAnExpiredSiblingToken_WhenHandlingVerifyEmail_ThenItIsLeftAlone` | Decision 2 boundary |
| `GivenAnotherPersonHoldsALiveToken_WhenHandlingVerifyEmail_ThenTheirTokenSurvives` | Decision 2 boundary |
| `GivenAlreadyVerifiedPerson_WhenHandlingVerifyEmail_ThenSucceedsAndConsumesTheToken` | Decision 4 |
| `GivenLogicallyDeletedPerson_WhenHandlingVerifyEmail_ThenEmailIsStillVerified` | Decision 5 |

## Step 4 — `VerifyEmailCommandHandler` (green)

Per the design's handler-flow table. Dependencies:
`IValidator<VerifyEmailCommand>`, `IAsyncReadOnlyRepository<EmailVerificationToken>`,
`IAsyncRepository<EmailVerificationToken>`, `IAsyncRepository<Person>`. Returns `DataOutput<…>`,
never throws. Each step commented with the UC/AF it implements.

Verify: `dotnet test --filter "Category=Unit"` green.

## Step 5 — Messages, endpoint, DI

- `AuthMessages.EmailVerifiedSuccessfully = "Email verified."`; map it to `Ok` in `AuthMessageMap`.
- `AuthController.VerifyEmail` — `[HttpPost("verify-email")]`, `[AllowAnonymous]`, dispatch through
  `CommandMediator`, `ResponseResolver.Resolve(result, statusMap: AuthMessageMap.StatusCodes)`.
- `Startup.AddDependencies` — register the validator and the handler alongside `ResetPasswordCommand`.

## Step 6 — Functional tests

`tests/Presentation/…WebApi.Tests/AuthControllerVerifyEmailTests.cs`, modeled on
`AuthControllerPasswordResetTests`: `[Collection(nameof(FunctionalCollection))]`,
`WebApiTest<Program>`, seeding helpers writing tokens directly (only a direct write produces one that
is already expired or used), every test asserting response **and** database state.

| Test | Covers |
| --- | --- |
| `GivenLiveToken_WhenPostVerifyEmail_ThenEmailIsVerifiedAndTokenConsumed` | main flow |
| `GivenUserOfAScope_WhenPostVerifyEmail_ThenEmailIsVerified` | the token identifies the person alone |
| `GivenExpiredToken_WhenPostVerifyEmail_ThenBadRequestAndEmailStaysUnverified` | AF-14a |
| `GivenUsedToken_WhenPostVerifyEmail_ThenBadRequestAndEmailStaysUnverified` | AF-14b |
| `GivenUnknownToken_WhenPostVerifyEmail_ThenBadRequest` | AF-14c |
| `GivenTokenDifferingOnlyInCase_WhenPostVerifyEmail_ThenBadRequest` | AF-14c, Decision 6 |
| `GivenMissingToken_WhenPostVerifyEmail_ThenBadRequest` | NFR-10 |
| `GivenNoBearerToken_WhenPostVerifyEmail_ThenEndpointAnswersAnonymously` | Decision 8 |
| `GivenTwoLiveTokens_WhenPostVerifyEmail_ThenBothAreConsumed` | Decision 2 |
| `GivenAnotherPersonHoldsALiveToken_WhenPostVerifyEmail_ThenTheirTokenSurvives` | Decision 2 boundary |
| `GivenAlreadyVerifiedPerson_WhenPostVerifyEmail_ThenSucceedsIdempotently` | Decision 4 |
| `GivenLogicallyDeletedPerson_WhenPostVerifyEmail_ThenEmailIsVerifiedButLoginStillFails` | Decision 5 |
| `GivenPersonCreated_WhenVerifyingWithTheIssuedToken_ThenEmailIsVerified` | UC-06 → UC-14 end to end |

Verify: `dotnet test --filter "Category=Functional"` green.

## Step 7 — Documentation

- `README.md` — UC-14 row `⬜` → `✅`; a note under the email-delivery section on what the
  verification page posts, mirroring the UC-13 paragraph.
- `Testing Specification Document.md` §10 — add `VerifyEmail` to the Command.Tests inventory and
  `AuthControllerVerifyEmail` to the WebApi.Tests inventory; update the suite totals.

## Step 8 — Full suite

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional"
```

Both green before Gate 3.

## Commits

- `refactor: share the token rejection messages between UC-13 and UC-14` (Step 0)
- `feat: verify an email address with a token` (Steps 1, 4, 5)
- `test: cover verifying an email with a token` (Steps 2, 3, 6, 7)
