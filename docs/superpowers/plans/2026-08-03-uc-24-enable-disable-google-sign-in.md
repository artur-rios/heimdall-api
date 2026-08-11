# UC-24: Enable/Disable Google Sign-In — Implementation Plan

Design: [2026-08-03-uc-24-enable-disable-google-sign-in-design.md](../specs/2026-08-03-uc-24-enable-disable-google-sign-in-design.md)
Issue: [#25](https://github.com/artur-rios/heimdall-api/issues/25)
Branch: `feature/uc-24-enable-disable-google-sign-in`

Sequenced test-first per the
[Testing Specification Document](../../requirements/Testing%20Specification%20Document.md) §9.

Steps marked **(A)** exist only if open question A is approved at the gate.

---

## Step 1 — Messages and status map

- `ScopeMessages` — add `GoogleSignInUpdatedSuccessfully`
  (`"Google Sign-In setting updated successfully."`), `NotScopeOwner`
  (`"You are not an owner of the target scope."`), and **(A)** `EnabledRequired`
  (`"Enabled is required."`), each with a doc comment naming UC-24 and its flow.
- `ScopeMessageMap` — `GoogleSignInUpdatedSuccessfully` → 200, `NotScopeOwner` → 403, **(A)**
  `EnabledRequired` → 400. `ScopeNotFound` (404) is already mapped and is reused for AF-24a.

## Step 2 — Command, validator and output

- `SetGoogleSignInCommand : BaseCommand, IActorScoped` — `Id` (route), `Enabled` (body;
  `bool?` under A, plain `bool` otherwise), `ActingPersonId`, `ActingRole`.
- **(A)** `SetGoogleSignInCommandValidator : AbstractValidator<SetGoogleSignInCommand>` —
  `RuleFor(c => c.Enabled).NotNull().WithMessage(ScopeMessages.EnabledRequired)`.
- `SetGoogleSignInCommandOutput : CommandOutput` — `Id`, `Name`, `Description`,
  `GoogleSignInEnabled`, `OwnerIds`, `CreatedAt`, `UpdatedAt` (Decisions 6 and 7).

## Step 3 — Handler tests (red)

`tests/Application/…Command.Tests/SetGoogleSignInCommandHandlerTests.cs`, reusing the seeding shape
of `UpdateScopeCommandHandlerTests` (scope in a `FakeRepository<Scope>`) plus the
`IScopeOwnershipChecker` Moq mock from `AddScopeOwnerCommandHandlerTests`.

| Test | Covers |
| --- | --- |
| `GivenSystemAdminAndEnabledTrue_WhenHandlingSetGoogleSignIn_ThenFlagIsEnabled` | main flow |
| `GivenSystemAdminAndEnabledFalse_WhenHandlingSetGoogleSignIn_ThenFlagIsDisabled` | main flow, the disable half |
| `GivenExistingOwnerActor_WhenHandlingSetGoogleSignIn_ThenFlagIsUpdated` | main flow, FR-GO-02 |
| `GivenScope_WhenHandlingSetGoogleSignIn_ThenUpdatedAtIsStamped` | Decision 8 |
| `GivenOutput_WhenHandlingSetGoogleSignIn_ThenItCarriesTheScopeWithPublicIdentifiersOnly` | SRD §4.0/NFR-15, Decision 6 |
| `GivenFlagAlreadyAtRequestedValue_WhenHandlingSetGoogleSignIn_ThenRequestSucceedsAndFlagIsUnchanged` | Decision 9 |
| `GivenUnknownScope_WhenHandlingSetGoogleSignIn_ThenScopeNotFoundIsReported` | AF-24a |
| `GivenLogicallyDeletedScope_WhenHandlingSetGoogleSignIn_ThenScopeNotFoundIsReported` | AF-24a, Decision 3 |
| `GivenScopeAdminNotOwningTheScope_WhenHandlingSetGoogleSignIn_ThenNotScopeOwnerIsReported` | AF-24b |
| **(A)** `GivenEnabledNotSupplied_WhenHandlingSetGoogleSignIn_ThenEnabledRequiredIsReported` | NFR-10, open question A |

Every refusal test also asserts the scope's `GoogleSignInEnabled` and `UpdatedAt` are unchanged.

**(A)** `tests/Application/…Command.Tests/SetGoogleSignInCommandValidatorTests.cs` — `true` and
`false` pass, `null` fails with `EnabledRequired`.

## Step 4 — Handler (green)

`SetGoogleSignInCommandHandler` implementing the flow from the design: **(A)** validate → scope
lookup → ownership check → owner id projection → set flag, stamp `UpdatedAt`, persist through
`scopeWriter.UpdateAsync` → return the scope. Each step commented with the UC/AF it implements;
failures returned as errors on `DataOutput`.

- Verify: `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"` green.

## Step 5 — Endpoint and DI

- `ScopeController` — add
  `[HttpPut("{id:guid}/google-signin")] SetGoogleSignIn(Guid id, [FromBody] SetGoogleSignInCommand command)`
  with `[RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]`, assigning `command.Id = id`,
  calling `HttpContext.ApplyActor(command)`, dispatching through `CommandMediator`, and resolving
  through `ScopeMessageMap.StatusCodes`. XML doc naming UC-24, FR-GO-01/FR-GO-02, and which flows the
  attribute versus the handler settles.
- `Startup.AddDependencies` — register
  `ICommandHandlerAsync<SetGoogleSignInCommand, SetGoogleSignInCommandOutput>`, and **(A)**
  `IValidator<SetGoogleSignInCommand>`. Placed next to the other scope command registrations. If A is
  declined, the registration carries the same "no validator, and why" comment UC-21 and UC-23 have.

## Step 6 — Functional tests

`tests/Presentation/…WebApi.Tests/ScopeControllerSetGoogleSignInTests.cs`, reusing the seeding shape
of `ScopeControllerUpdateTests` (`db.CreateContext()`, `UniqueName()`) plus the owner seeding of
`PersonControllerAddScopeOwnerTests`, authorised with `TestTokens.ForRole` / `TestTokens.For`.

| Test | Covers |
| --- | --- |
| `GivenSystemAdmin_WhenPutGoogleSignInEnabled_ThenOkAndFlagIsEnabled` | main flow + database state |
| `GivenSystemAdminAndEnabledScope_WhenPutGoogleSignInDisabled_ThenOkAndFlagIsDisabled` | main flow, the disable half |
| `GivenExistingOwner_WhenPutGoogleSignIn_ThenOkAndFlagIsEnabled` | main flow, FR-GO-02 |
| `GivenScopeAdminOfAnotherScope_WhenPutGoogleSignIn_ThenForbidden` | AF-24b |
| `GivenUserRole_WhenPutGoogleSignIn_ThenForbidden` | precondition (attribute) |
| `GivenUnknownScope_WhenPutGoogleSignIn_ThenNotFound` | AF-24a |
| `GivenLogicallyDeletedScope_WhenPutGoogleSignIn_ThenNotFound` | AF-24a, Decision 3 |
| `GivenNoToken_WhenPutGoogleSignIn_ThenUnauthorized` | precondition |
| **(A)** `GivenEmptyBody_WhenPutGoogleSignIn_ThenBadRequestAndFlagIsUnchanged` | open question A |

Refusal tests assert the persisted `google_sign_in_enabled` did not move; the success tests assert
the inverse.

## Step 7 — Documentation

- `Testing Specification Document.md` §10: add `SetGoogleSignIn` to the Command.Tests row, note
  `ScopeControllerSetGoogleSignInTests`, and update the suite totals line to UC-24 (measured, not
  incremented).
- `README.md`: mark UC-24 ✅ in the use case tracker.

## Step 8 — Full suite

`dotnet test src/ArturRios.Heimdall.sln` — both categories green before the pull request. The
pull request body references the use case and `Closes #25`.
