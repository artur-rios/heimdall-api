# UC-15: Resend Verification Email — Implementation Plan

Design: [2026-07-30-uc-15-resend-verification-email-design.md](../specs/2026-07-30-uc-15-resend-verification-email-design.md)
Issue: [#16](https://github.com/artur-rios/heimdall-api/issues/16)
Branch: `feature/uc-15-resend-verification-email`

Sequenced test-first per the
[Testing Specification Document](../../requirements/Testing%20Specification%20Document.md) §9.

## Step 0 — Messages and status map

- `AuthMessages`: add `VerificationEmailSent` (`"Verification email sent."`),
  `EmailAlreadyVerified` (`"Email already verified."`), `PersonNotFound` (`"Person not found."`).
- `AuthMessageMap`: `VerificationEmailSent` → 200, `EmailAlreadyVerified` → 400,
  `PersonNotFound` → 404. Update the class summary to name UC-15.
- Verify: `dotnet build` clean.

## Step 1 — Command and output

- `ResendVerificationEmailCommand : BaseCommand, IActorScoped` — `ActingPersonId`, `ActingRole`, and
  nothing else (Decision 1: no validator, no body).
- `ResendVerificationEmailCommandOutput : CommandOutput` — empty (Decision 7).

## Step 2 — Handler tests (red)

`tests/Application/…Command.Tests/ResendVerificationEmailCommandHandlerTests.cs`, mirroring
`VerifyEmailCommandHandlerTests`: `AsyncFakeRepository<Person>` / `<EmailVerificationToken>`, and a
Moq `IEmailVerificationService` — the send is the observable half of the main flow, so it is verified,
not stubbed away.

| Test | Covers |
| --- | --- |
| `GivenUnverifiedPerson_WhenHandlingResendVerificationEmail_ThenVerificationEmailIsIssuedAndSent` | main flow, FR-EV-04 |
| `GivenUnverifiedPersonWithNoTokens_WhenHandlingResendVerificationEmail_ThenEmailIsStillSent` | main flow, empty-token boundary |
| `GivenOutstandingLiveToken_WhenHandlingResendVerificationEmail_ThenItIsRetired` | UC-15 step 3, Decision 2 |
| `GivenTwoOutstandingLiveTokens_WhenHandlingResendVerificationEmail_ThenBothAreRetired` | Decision 2 |
| `GivenAnExpiredToken_WhenHandlingResendVerificationEmail_ThenItIsLeftAlone` | Decision 2 boundary |
| `GivenAnotherPersonHoldsALiveToken_WhenHandlingResendVerificationEmail_ThenTheirTokenSurvives` | Decision 2 boundary |
| `GivenAlreadyVerifiedPerson_WhenHandlingResendVerificationEmail_ThenEmailAlreadyVerifiedIsReported` | AF-15a |
| `GivenAlreadyVerifiedPerson_WhenHandlingResendVerificationEmail_ThenNothingIsRetiredAndNothingIsSent` | AF-15a side effects |
| `GivenActorNamesNoExistingPerson_WhenHandlingResendVerificationEmail_ThenPersonNotFoundIsReported` | Decision 3 |
| `GivenLogicallyDeletedPerson_WhenHandlingResendVerificationEmail_ThenEmailIsStillSent` | Decision 5 |

## Step 3 — `ResendVerificationEmailCommandHandler` (green)

Per the design's handler-flow table. Dependencies:
`IAsyncReadOnlyRepository<Person>`, `IAsyncReadOnlyRepository<EmailVerificationToken>`,
`IAsyncRepository<EmailVerificationToken>`, `IEmailVerificationService`. Returns `DataOutput<…>`,
never throws, and adds no try/catch around the send (Decision 6 — the sender already swallows delivery
failures). Each step commented with the UC/AF it implements. Retirement reuses the shape `VerifyEmailCommandHandler.ConsumeTokensAsync` established:
`!Used && ExpiresAt > now`, filtered to the person, one `UpdateAsync` per row, first failure returned.

- Verify: `dotnet test --filter "Category=Unit"` green.

## Step 4 — Endpoint and DI

- `AuthController.ResendVerification` — `[HttpPost("resend-verification")]`, **no**
  `[AllowAnonymous]` and no `[RoleRequirement]` (Decision 8). Builds the command and calls
  `HttpContext.ApplyActor(command)`, as `PersonController.Delete` does. Resolves through
  `AuthMessageMap.StatusCodes`. XML doc naming UC-15, FR-EV-04, AF-15a, and why it is the one
  authenticated endpoint on this controller.
- `Startup.AddDependencies`: register
  `ICommandHandlerAsync<ResendVerificationEmailCommand, ResendVerificationEmailCommandOutput>`.
  No validator registration (Decision 1).

## Step 5 — Functional tests

`tests/Presentation/…WebApi.Tests/AuthControllerResendVerificationEmailTests.cs`, mirroring
`AuthControllerVerifyEmailTests`' seeding helpers, authorised with `TestTokens.For(person.PublicId, role)`.
`Gateway.PostAsync` requires a payload argument, so the bodyless request is issued with `new { }`; the
action binds no body, so it is ignored.

| Test | Covers |
| --- | --- |
| `GivenAuthenticatedSystemAdmin_WhenPostResendVerification_ThenNewTokenIsIssued` | main flow |
| `GivenAuthenticatedScopeAdmin_WhenPostResendVerification_ThenNewTokenIsIssued` | main flow, role |
| `GivenAuthenticatedUserOfAScope_WhenPostResendVerification_ThenNewTokenIsIssued` | main flow, role |
| `GivenOutstandingLiveToken_WhenPostResendVerification_ThenItIsRetiredAndOnlyTheNewOneIsLive` | UC-15 step 3 |
| `GivenAnExpiredToken_WhenPostResendVerification_ThenItIsLeftAlone` | Decision 2 boundary |
| `GivenAnotherPersonHoldsALiveToken_WhenPostResendVerification_ThenTheirTokenSurvives` | Decision 2 boundary |
| `GivenAlreadyVerifiedPerson_WhenPostResendVerification_ThenBadRequestAndNoTokenIsIssued` | AF-15a |
| `GivenNoBearerToken_WhenPostResendVerification_ThenUnauthorized` | precondition |
| `GivenTokenNamingNoExistingPerson_WhenPostResendVerification_ThenNotFound` | Decision 3 |
| `GivenLogicallyDeletedPerson_WhenPostResendVerification_ThenNewTokenIsIssued` | Decision 5 |
| `GivenPersonCreated_WhenResendingThenVerifying_ThenOldLinkIsDeadAndNewOneWorks` | UC-06 → UC-15 → UC-14 |

- Verify: `dotnet test --filter "Category=Functional"` green.

## Step 6 — Documentation

- `Testing Specification Document.md` §10: add `ResendVerificationEmail` to the Command.Tests row and
  `AuthControllerResendVerification` to the WebApi.Tests row; update the suite totals line to UC-15.
- `README.md` §Email delivery: the paragraph on token retirement (currently framed around *spending*
  a token) gains the resend — `POST /api/auth/resend-verification`, authenticated, retires the
  outstanding links before mailing a new one.

## Step 7 — Full suite

`dotnet test src/ArturRios.Heimdall.sln` — both categories green before the pull request.
