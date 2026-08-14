# Auth Responses Report Email Verification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish `emailVerified` on every authentication response that hands out a full token, and stop `GoogleUser.EmailVerified` going stale on repeat sign-ins.

**Architecture:** Three command output DTOs each gain one property, filled from data the handler already holds — the `Person` row for the two password paths, the verified Google ID token payload for the Google path. The Google handler additionally writes the payload's value back to the stored row when the two disagree. No token claim changes, no new endpoints, no schema migration.

**Tech Stack:** .NET 9 / C#, xUnit with Moq and Bogus, `AsyncFakeRepository` for unit tests, Testcontainers-backed PostgreSQL for functional tests, FluentValidation, EF Core.

**Spec:** `docs/superpowers/specs/2026-08-14-auth-responses-report-email-verified-design.md`

## Global Constraints

- Branch: `feat/auth-response-email-verified` (already checked out; the spec commit is on it).
- Test naming is Given-When-Then: `GivenSomeCondition_WhenSomeAction_ThenSomeOutput`.
- Unit tests carry `[UnitFact]`; functional tests carry `[FunctionalFact]` and live in a class with `[Collection(nameof(FunctionalCollection))]`.
- Run unit tests with `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"`, functional with `--filter "Category=Functional"`. Never run both filters in one command.
- Every new public property carries an XML doc comment, matching the surrounding style in each file.
- The property is named `EmailVerified` in C# and serializes as `emailVerified`, matching `PersonOutput.EmailVerified`.
- Do not refresh `GoogleUser.Name`, `Email`, or `ProfilePictureUrl` — out of scope.
- Commit after each task with a Conventional Commits subject, lowercase, ≤50 chars.

## File Structure

| File | Responsibility | Task |
| ---- | -------------- | ---- |
| `src/Application/ArturRios.Heimdall.Command/Output/LoginCommandOutput.cs` | Gains `bool? EmailVerified` | 1 |
| `src/Application/ArturRios.Heimdall.Command/Handlers/LoginCommandHandler.cs` | Fills it on the full-token branch only | 1 |
| `tests/Application/ArturRios.Heimdall.Command.Tests/LoginCommandHandlerTests.cs` | Unit coverage | 1 |
| `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/AuthControllerLoginTests.cs` | Functional coverage | 1 |
| `src/Application/ArturRios.Heimdall.Command/Output/VerifyTwoFactorAuthCommandOutput.cs` | Gains `bool EmailVerified` | 2 |
| `src/Application/ArturRios.Heimdall.Command/Handlers/VerifyTwoFactorAuthCommandHandler.cs` | Fills it from the loaded person | 2 |
| `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/AuthControllerVerifyTwoFactorAuthTests.cs` | Functional coverage | 2 |
| `src/Application/ArturRios.Heimdall.Command/Output/GoogleSignInCommandOutput.cs` | Gains `bool EmailVerified` | 3 |
| `src/Application/ArturRios.Heimdall.Command/Handlers/GoogleSignInCommandHandler.cs` | Fills it from the payload (task 3); refreshes the stored column (task 4) | 3, 4 |
| `tests/Application/ArturRios.Heimdall.Command.Tests/GoogleSignInCommandHandlerTests.cs` | Unit coverage | 3, 4 |
| `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/AuthControllerGoogleSignInTests.cs` | Functional coverage | 3, 4 |
| `docs/requirements/System Requirements Document.md` | FR-EV-05, FR-GO-19, traceability | 5 |
| `docs/requirements/Use Case Specification Document.md` | UC-11, UC-25, UC-38 | 5 |
| `docs/content/en/docs/getting-started.md`, `docs/content/en/docs/flows/*.md` | Site docs | 5 |
| `docs/openapi/heimdall.json` | Regenerated | 5 |

There is no unit test file for `VerifyTwoFactorAuthCommandHandler` (the class is covered functionally by `AuthControllerVerifyTwoFactorAuthTests`), so task 2 adds no unit test — do not create a new unit test class for it.

---

### Task 1: Login reports the person's verification status

**Files:**
- Modify: `src/Application/ArturRios.Heimdall.Command/Output/LoginCommandOutput.cs`
- Modify: `src/Application/ArturRios.Heimdall.Command/Handlers/LoginCommandHandler.cs:94-97` (the success return) and `:135-140` (the challenge return)
- Test: `tests/Application/ArturRios.Heimdall.Command.Tests/LoginCommandHandlerTests.cs`
- Test: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/AuthControllerLoginTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `LoginCommandOutput.EmailVerified` of type `bool?` — `null` whenever `RequiresTwoFactor` is `true`.

- [ ] **Step 1: Write the failing unit tests**

Append to `LoginCommandHandlerTests.cs`. The existing helper `Person(long id, string email, Roles role, ...)` does not set `EmailVerified`, so it defaults to `false`; set it explicitly in both tests so the expectation is visible rather than incidental. `Command(string email, ...)` is the file's existing helper for building the input.

```csharp
    [UnitFact]
    public async Task GivenVerifiedPerson_WhenHandlingLogin_ThenOutputReportsEmailVerifiedTrue()
    {
        // Given a person whose address has been verified (FR-EV-05)
        var person = Person(10, "verified@test.local", Roles.SystemAdmin);
        person.EmailVerified = true;
        var handler = FixtureFor(await PersonsWith(person)).Handler();

        // When
        var output = await handler.HandleAsync(Command("verified@test.local"));

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.EmailVerified);
    }

    [UnitFact]
    public async Task GivenUnverifiedPerson_WhenHandlingLogin_ThenOutputReportsEmailVerifiedFalse()
    {
        // Given a person who has not yet clicked their verification link — the case the field
        // exists for, since the client must know to prompt them (UC-15)
        var person = Person(10, "unverified@test.local", Roles.SystemAdmin);
        person.EmailVerified = false;
        var handler = FixtureFor(await PersonsWith(person)).Handler();

        // When
        var output = await handler.HandleAsync(Command("unverified@test.local"));

        // Then
        Assert.True(output.Success);
        Assert.False(output.Data!.EmailVerified);
    }
```

For the AF-11g case, do not add a new test — extend the existing
`GivenPersonWithActiveTwoFactorAuth_WhenHandlingLogin_ThenChallengeTokenIsIssuedInsteadOfFullToken`
at line 404. It already asserts `Token` and `ExpiresAt` are null; the new assertion belongs in the
same group. Add the person's verified flag to its Given block and one assertion to its Then block:

```csharp
        // Given — AF-11g (FR-2F-07): correct credentials, but active 2FA
        var person = Person(10, "admin@test.local", Roles.SystemAdmin);
        person.EmailVerified = true;
```

```csharp
        Assert.Null(output.Data.Token);
        Assert.Null(output.Data.ExpiresAt);
        // The caller has passed only the first factor, so the challenge response says nothing about
        // the account — they get this from /api/auth/2fa/verify instead (UC-38).
        Assert.Null(output.Data.EmailVerified);
```

Setting `EmailVerified = true` in the Given is what makes the assertion meaningful: `null` is then
distinguishable from the person's actual value rather than coinciding with a `false` default.

- [ ] **Step 2: Run the unit tests to verify they fail**

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit" 2>&1 | tail -20
```

Expected: compilation error — `LoginCommandOutput` has no `EmailVerified`.

- [ ] **Step 3: Add the property**

In `LoginCommandOutput.cs`, after the `ExpiresAt` property:

```csharp
    /// <summary>
    ///     Whether the authenticated person's email address is verified (FR-EV-05), so a caller
    ///     knows whether to prompt them and offer <c>POST /api/auth/resend-verification</c> (UC-15).
    ///     Null when <see cref="RequiresTwoFactor" /> is true: the caller has passed only the first
    ///     factor and is not authenticated yet, so this response tells them nothing about the
    ///     account. They receive it from <c>POST /api/auth/2fa/verify</c> instead (UC-38).
    /// </summary>
    public bool? EmailVerified { get; set; }
```

Also extend the class-level `<summary>`: replace the sentence "nothing about the person is repeated here." with "the only thing about the person repeated here is <see cref="EmailVerified" />, which no claim carries."

- [ ] **Step 4: Fill it in the handler**

In `LoginCommandHandler.HandleAsync`, change the success return to:

```csharp
        return output
            .WithData(new LoginCommandOutput
            {
                Token = token.Token, ExpiresAt = token.ExpiresAt, EmailVerified = person.EmailVerified
            })
            .WithMessage(AuthMessages.LoginSuccessful);
```

Leave `IssueChallengeAsync` untouched — its `LoginCommandOutput` initializer sets no `EmailVerified`, so it stays `null`, which is the specified behaviour.

- [ ] **Step 5: Run the unit tests to verify they pass**

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit" 2>&1 | tail -20
```

Expected: PASS, with no previously-passing test now failing.

- [ ] **Step 6: Write the failing functional test**

Append to `AuthControllerLoginTests.cs`. Note `SeedPersonAsync` hardcodes `EmailVerified = true`; add an optional parameter rather than a second helper — change its signature to:

```csharp
    private async Task<Person> SeedPersonAsync(
        Roles role, string email, bool isDeleted = false, string password = Password,
        bool emailVerified = true)
```

and inside the initializer replace `EmailVerified = true,` with `EmailVerified = emailVerified,`. Then add:

```csharp
    [FunctionalFact]
    public async Task GivenUnverifiedPerson_WhenPostAuthLogin_ThenResponseReportsEmailVerifiedFalse()
    {
        // Given an admin who never confirmed their address (FR-EV-05)
        var person = await SeedPersonAsync(
            Roles.SystemAdmin, UniqueEmail("unverified"), emailVerified: false);

        // When
        var response = await LoginAsync(person.Email);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Body!.Data!.EmailVerified);
    }

    [FunctionalFact]
    public async Task GivenVerifiedPerson_WhenPostAuthLogin_ThenResponseReportsEmailVerifiedTrue()
    {
        // Given an admin who confirmed their address
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("verified"));

        // When
        var response = await LoginAsync(person.Email);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body!.Data!.EmailVerified);
    }
```

- [ ] **Step 7: Run the functional tests**

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional" 2>&1 | tail -20
```

Expected: PASS. (The implementation from steps 3–4 is already in place, so these pass on first run; they exist to prove the value survives serialization, which the unit tests cannot show.)

- [ ] **Step 8: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Command tests/Application/ArturRios.Heimdall.Command.Tests tests/Presentation/ArturRios.Heimdall.WebApi.Tests
git commit -m "feat: report email verification on login"
```

---

### Task 2: Two-factor verification reports the same status

**Files:**
- Modify: `src/Application/ArturRios.Heimdall.Command/Output/VerifyTwoFactorAuthCommandOutput.cs`
- Modify: `src/Application/ArturRios.Heimdall.Command/Handlers/VerifyTwoFactorAuthCommandHandler.cs:121-123` (the success return)
- Test: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/AuthControllerVerifyTwoFactorAuthTests.cs`

**Interfaces:**
- Consumes: nothing — task 1's `bool?` is a different type on a different class; do not reuse it here.
- Produces: `VerifyTwoFactorAuthCommandOutput.EmailVerified` of type `bool` (non-nullable).

- [ ] **Step 1: Write the failing functional test**

In `AuthControllerVerifyTwoFactorAuthTests.cs`, `SeedPersonAsync(Roles role, string email)` hardcodes `EmailVerified = true`. Add the parameter the same way task 1 did:

```csharp
    private async Task<Person> SeedPersonAsync(Roles role, string email, bool emailVerified = true)
```

and replace `EmailVerified = true` in its initializer with `EmailVerified = emailVerified`.

Then append this test. Its setup is the same sequence as the existing
`GivenFullTwoFactorFlow_WhenEnablingConfirmingLoggingInAndVerifying_ThenFullTokenIsIssued`
at line 141 — enable App-based 2FA through the real endpoints, confirm it, log in to get a
challenge token, redeem it — with the person seeded unverified and the assertions changed:

```csharp
    [FunctionalFact]
    public async Task GivenUnverifiedGatedPerson_WhenPostTwoFactorVerify_ThenResponseReportsEmailVerifiedFalse()
    {
        // Given a 2FA-gated person whose address is unverified: login gave them a challenge that
        // deliberately said nothing about the account, so this is where they learn it (FR-EV-05)
        var person = await SeedPersonAsync(
            Roles.SystemAdmin, UniqueEmail("gated-unverified"), emailVerified: false);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));
        var secret = await EnableAppAsync("App");
        var confirm = await ConfirmAsync(CurrentTotpCode(secret));
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var login = await LoginAsync(person.Email);
        Assert.True(login.Body!.Data!.RequiresTwoFactor);

        // When redeeming the challenge token with the current app code
        var verify = await VerifyAsync(login.Body.Data.ChallengeToken!, CurrentTotpCode(secret));

        // Then — the full token, and the verification status the challenge withheld
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.NotNull(verify.Body?.Data?.Token);
        Assert.False(verify.Body!.Data!.EmailVerified);
    }
```

`VerifyAsync(string challengeToken, string? code = null, string? recoveryCode = null)` takes the app code as its second positional argument — pass it positionally as shown, not as a named `appCode`, which is not the parameter's name.

- [ ] **Step 2: Run the functional tests to verify the new one fails**

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional" 2>&1 | tail -20
```

Expected: compilation error — `VerifyTwoFactorAuthCommandOutput` has no `EmailVerified`.

- [ ] **Step 3: Add the property**

In `VerifyTwoFactorAuthCommandOutput.cs`, after `ExpiresAt`:

```csharp
    /// <summary>
    ///     Whether the authenticated person's email address is verified (FR-EV-05), reported here
    ///     rather than on UC-11's challenge response — the same value a direct login returns, since a
    ///     UC-38 login ends exactly like an ungated one.
    /// </summary>
    public bool EmailVerified { get; set; }
```

- [ ] **Step 4: Fill it in the handler**

In `VerifyTwoFactorAuthCommandHandler.HandleAsync`, change the success return to:

```csharp
        return output
            .WithData(new VerifyTwoFactorAuthCommandOutput
            {
                Token = token.Token, ExpiresAt = token.ExpiresAt, EmailVerified = person.EmailVerified
            })
            .WithMessage(TwoFactorMessages.VerificationSuccessful);
```

`person` is the local already loaded at line 55 and non-null past the guard at line 67.

- [ ] **Step 5: Run the functional tests to verify they pass**

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional" 2>&1 | tail -20
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Command tests/Presentation/ArturRios.Heimdall.WebApi.Tests
git commit -m "feat: report email verification on 2fa verify"
```

---

### Task 3: Google sign-in reports the payload's verification status

**Files:**
- Modify: `src/Application/ArturRios.Heimdall.Command/Output/GoogleSignInCommandOutput.cs`
- Modify: `src/Application/ArturRios.Heimdall.Command/Handlers/GoogleSignInCommandHandler.cs:97-99` (the success return)
- Test: `tests/Application/ArturRios.Heimdall.Command.Tests/GoogleSignInCommandHandlerTests.cs`
- Test: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/AuthControllerGoogleSignInTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `GoogleSignInCommandOutput.EmailVerified` of type `bool` (non-nullable), sourced from `GoogleIdTokenPayload.EmailVerified`.

- [ ] **Step 1: Write the failing unit tests**

Append to `GoogleSignInCommandHandlerTests.cs`. The file's `Payload(...)` helper already takes `emailVerified`, and `SeedGoogleUserAsync` uses Bogus, which randomises `EmailVerified` — so the stored value must be pinned explicitly where it matters. Add a parameter to that helper:

```csharp
    private static async Task<GoogleUser> SeedGoogleUserAsync(
        AsyncFakeRepository<GoogleUser> googleUsers,
        Scope scope,
        string googleId = GoogleSubject,
        string email = Email,
        bool isDeleted = false,
        bool emailVerified = true)
```

and add `.RuleFor(x => x.EmailVerified, _ => emailVerified)` to the Bogus chain, alongside the existing `RuleFor` calls.

Then the tests:

```csharp
    [UnitFact]
    public async Task GivenPayloadReportsVerifiedAddress_WhenHandlingGoogleSignIn_ThenOutputReportsEmailVerifiedTrue()
    {
        // Given Google asserting the address is verified (FR-EV-05)
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(emailVerified: true)), scopes, new AsyncFakeRepository<Person>(),
            googleUsers, googleUsers, TokenIssuer().issuer);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.EmailVerified);
    }

    [UnitFact]
    public async Task GivenPayloadReportsUnverifiedAddress_WhenHandlingGoogleSignIn_ThenOutputReportsEmailVerifiedFalse()
    {
        // Given Google asserting the address is not verified — email_verified can be false
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(emailVerified: false)), scopes, new AsyncFakeRepository<Person>(),
            googleUsers, googleUsers, TokenIssuer().issuer);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.False(output.Data!.EmailVerified);
    }

    [UnitFact]
    public async Task GivenStoredValueDisagreesWithPayload_WhenHandlingGoogleSignIn_ThenOutputReportsThePayload()
    {
        // Given a returning Google User stored as unverified whose token now says verified: the
        // token just verified in this request is the fresher truth (design: source of the value)
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        await SeedGoogleUserAsync(googleUsers, scope, emailVerified: false);
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(emailVerified: true)), scopes, new AsyncFakeRepository<Person>(),
            googleUsers, googleUsers, TokenIssuer().issuer);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.EmailVerified);
    }
```

- [ ] **Step 2: Run the unit tests to verify they fail**

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit" 2>&1 | tail -20
```

Expected: compilation error — `GoogleSignInCommandOutput` has no `EmailVerified`.

- [ ] **Step 3: Add the property**

In `GoogleSignInCommandOutput.cs`, after `ExpiresAt`:

```csharp
    /// <summary>
    ///     Whether Google asserts the account's email address is verified (FR-EV-05), read from the
    ///     ID token verified in this same request rather than from the stored row — the token is the
    ///     fresher of the two.
    /// </summary>
    public bool EmailVerified { get; set; }
```

- [ ] **Step 4: Fill it in the handler**

In `GoogleSignInCommandHandler.HandleAsync`, change the success return to:

```csharp
        return output
            .WithData(new GoogleSignInCommandOutput
            {
                Token = token.Token, ExpiresAt = token.ExpiresAt, EmailVerified = payload.EmailVerified
            })
            .WithMessage(AuthMessages.GoogleSignInSuccessful);
```

- [ ] **Step 5: Run the unit tests to verify they pass**

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit" 2>&1 | tail -20
```

Expected: PASS.

- [ ] **Step 6: Write the functional test**

In `AuthControllerGoogleSignInTests.cs`, add the parameter to the seeding helper — change its signature to:

```csharp
    private async Task<GoogleUser> SeedGoogleUserAsync(
        Scope scope, string googleId, string email, bool isDeleted = false, bool emailVerified = true)
```

and replace `EmailVerified = true,` in its initializer with `EmailVerified = emailVerified,`. Then add:

```csharp
    [FunctionalFact]
    public async Task GivenTokenReportsUnverifiedAddress_WhenPostAuthGoogle_ThenResponseReportsEmailVerifiedFalse()
    {
        // Given a first sign-in with a Google token whose email_verified claim is false (FR-EV-05)
        var scope = await SeedScopeAsync();

        // When
        var response = await SignInAsync(
            scope.PublicId, TestGoogleTokens.For(emailVerified: false));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Body!.Data!.EmailVerified);
    }
```

- [ ] **Step 7: Run the functional tests**

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional" 2>&1 | tail -20
```

Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Command tests/Application/ArturRios.Heimdall.Command.Tests tests/Presentation/ArturRios.Heimdall.WebApi.Tests
git commit -m "feat: report email verification on google sign-in"
```

---

### Task 4: Refresh the stored Google verification flag on sign-in

**Files:**
- Modify: `src/Application/ArturRios.Heimdall.Command/Handlers/GoogleSignInCommandHandler.cs` (between the AF-25d check ending at line 85 and the token issue at line 91)
- Test: `tests/Application/ArturRios.Heimdall.Command.Tests/GoogleSignInCommandHandlerTests.cs`
- Test: `tests/Presentation/ArturRios.Heimdall.WebApi.Tests/AuthControllerGoogleSignInTests.cs`

**Interfaces:**
- Consumes: task 3's `GoogleSignInCommandOutput.EmailVerified`, and the `SeedGoogleUserAsync(..., bool emailVerified = true)` parameters task 3 added to both test files.
- Produces: no new public surface — a private method `RefreshEmailVerifiedAsync(GoogleUser googleUser, GoogleIdTokenPayload payload)` returning `Task`.

- [ ] **Step 1: Write the failing unit tests**

Append to `GoogleSignInCommandHandlerTests.cs`:

```csharp
    [UnitFact]
    public async Task GivenStoredValueIsStale_WhenHandlingGoogleSignIn_ThenStoredValueIsRefreshedFromTheToken()
    {
        // Given a returning Google User stored as unverified whose address has since been verified
        // at Google: FR-GO-10 must not leave the row stale forever (FR-GO-19)
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        await SeedGoogleUserAsync(googleUsers, scope, emailVerified: false);
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(emailVerified: true)), scopes, new AsyncFakeRepository<Person>(),
            googleUsers, googleUsers, TokenIssuer().issuer);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then — the sign-in succeeded and the row now agrees with Google
        Assert.True(output.Success);
        var stored = (await googleUsers.GetAllAsync()).Data!.Single();
        Assert.True(stored.EmailVerified);
    }

    [UnitFact]
    public async Task GivenGoogleRevokedVerification_WhenHandlingGoogleSignIn_ThenStoredValueIsRefreshedToFalse()
    {
        // Given the refresh running in the other direction too — the rule is "match the token",
        // not "only ever turn the flag on"
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        await SeedGoogleUserAsync(googleUsers, scope, emailVerified: true);
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(emailVerified: false)), scopes, new AsyncFakeRepository<Person>(),
            googleUsers, googleUsers, TokenIssuer().issuer);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.True(output.Success);
        var stored = (await googleUsers.GetAllAsync()).Data!.Single();
        Assert.False(stored.EmailVerified);
    }

    [UnitFact]
    public async Task GivenStoredValueAlreadyAgrees_WhenHandlingGoogleSignIn_ThenNoUpdateIsWritten()
    {
        // Given a row already matching the token: the ordinary sign-in path stays read-only
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        await SeedGoogleUserAsync(googleUsers, scope, emailVerified: true);
        var writer = new Mock<IAsyncRepository<GoogleUser>>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(emailVerified: true)), scopes, new AsyncFakeRepository<Person>(),
            googleUsers, writer.Object, TokenIssuer().issuer);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.True(output.Success);
        writer.Verify(w => w.UpdateAsync(It.IsAny<GoogleUser>()), Times.Never);
    }
```

The last test needs `using ArturRios.Data.Relational.Core.Interfaces;` at the top of the file if it is not already there — check before adding, the file may already import it.

- [ ] **Step 2: Run the unit tests to verify the first two fail**

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit" 2>&1 | tail -30
```

Expected: `GivenStoredValueIsStale_...` and `GivenGoogleRevokedVerification_...` FAIL on the stored-value assertion. `GivenStoredValueAlreadyAgrees_...` passes already — that is fine; it is a regression guard, not a driver.

- [ ] **Step 3: Implement the refresh**

In `GoogleSignInCommandHandler.HandleAsync`, insert between the sign-up/AF-25d block (ending line 85) and the comment introducing UC-25 step 8:

```csharp
        // FR-GO-19: the row was populated from the token at sign-up (FR-GO-09) and never touched
        // again, so a Google account verified after its first sign-in here kept a stale flag. The
        // token just verified is the fresher truth, so the column is brought back into line — a
        // no-op on a sign-up, where the two already agree by construction.
        await RefreshEmailVerifiedAsync(googleUser, payload);
```

and add the private method after `HandleAsync`, before `SignUpAsync`:

```csharp
    /// <summary>
    ///     FR-GO-19: writes the verified token's <c>email_verified</c> back to the stored row when
    ///     the two disagree, so a returning Google User's flag does not stay frozen at whatever it
    ///     was on their first sign-in. Nothing is written when they already agree, keeping the
    ///     ordinary sign-in path read-only.
    /// </summary>
    /// <remarks>
    ///     A failed write does not fail the sign-in. The caller has proved the account is theirs and
    ///     the token is theirs to receive; a flag that could not be refreshed is a data-freshness
    ///     problem, not an authentication one — the same judgement
    ///     <c>LoginCommandHandler.IssueFreshEmailCodeAsync</c> makes about a delivery failure. The
    ///     response reports the payload's value either way, so the caller is told the truth
    ///     regardless of whether the write landed.
    /// </remarks>
    private async Task RefreshEmailVerifiedAsync(GoogleUser googleUser, GoogleIdTokenPayload payload)
    {
        if (googleUser.EmailVerified == payload.EmailVerified)
        {
            return;
        }

        googleUser.EmailVerified = payload.EmailVerified;

        await googleUserWriter.UpdateAsync(googleUser);
    }
```

- [ ] **Step 4: Run the unit tests to verify they pass**

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit" 2>&1 | tail -20
```

Expected: PASS, including the existing `GivenExistingGoogleUser_WhenHandlingGoogleSignIn_ThenIssuesTokenWithoutCreatingDuplicate`, which must still find exactly one row.

- [ ] **Step 5: Write the functional test**

Append to `AuthControllerGoogleSignInTests.cs`:

```csharp
    [FunctionalFact]
    public async Task GivenStoredValueIsStale_WhenPostAuthGoogle_ThenStoredValueIsRefreshed()
    {
        // Given a Google User registered while their address was unverified, signing in again with
        // a token that now says verified (FR-GO-19)
        var scope = await SeedScopeAsync();
        var subject = $"google-sub-{Guid.NewGuid():N}";
        var email = UniqueEmail("stale");
        await SeedGoogleUserAsync(scope, subject, email, emailVerified: false);

        // When
        var response = await SignInAsync(
            scope.PublicId, TestGoogleTokens.For(subject, email, emailVerified: true));

        // Then — the response and the persisted row both report the token's value
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body!.Data!.EmailVerified);
        var stored = Assert.Single(await StoredAsync(scope));
        Assert.True(stored.EmailVerified);
    }
```

- [ ] **Step 6: Run the functional tests**

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional" 2>&1 | tail -20
```

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Application/ArturRios.Heimdall.Command tests/Application/ArturRios.Heimdall.Command.Tests tests/Presentation/ArturRios.Heimdall.WebApi.Tests
git commit -m "fix: refresh stored google email verification"
```

---

### Task 5: Requirements, use cases, site docs, and the published contract

**Files:**
- Modify: `docs/requirements/System Requirements Document.md:167-174` (section 3.6), `:194-211` (section 3.8), `:802-803` (traceability)
- Modify: `docs/requirements/Use Case Specification Document.md:648` (UC-11 postcondition), `:666`, `:685`, `:1250`, `:1670`
- Modify: `docs/content/en/docs/getting-started.md:131`
- Modify: `docs/content/en/docs/flows/login.md:55,57`, `docs/content/en/docs/flows/google-sign-in.md:74`, `docs/content/en/docs/flows/two-factor.md:118`
- Modify: `docs/openapi/heimdall.json` (regenerated, never hand-edited)

**Interfaces:**
- Consumes: the three properties added in tasks 1–3 and the refresh from task 4.
- Produces: nothing code-facing.

- [ ] **Step 1: Add FR-EV-05**

In `System Requirements Document.md`, append to the section 3.6 table, after the FR-EV-04 row:

```markdown
| FR-EV-05 | On successful authentication, the system shall report whether the authenticated person's email address is verified | Medium |
```

- [ ] **Step 2: Add FR-GO-19**

Append to the section 3.8 table, after the FR-GO-18 row:

```markdown
| FR-GO-19 | On each sign-in with an existing Google User, the system shall refresh the stored `EmailVerified` from the verified Google ID token claims | Medium |
```

- [ ] **Step 3: Extend the traceability ranges**

At line 802-803, change `FR-EV-01 through FR-EV-04` to `FR-EV-01 through FR-EV-05`, and `FR-GO-01 through FR-GO-18` to `FR-GO-01 through FR-GO-19`.

- [ ] **Step 4: Update the use case diagrams and postcondition**

In `Use Case Specification Document.md`:

- Lines 666 and 685 (UC-11, both role flows): change `200 OK { token, expiresAt }` to `200 OK { token, expiresAt, emailVerified }`.
- Line 1250 (UC-25) and line 1670 (UC-38): the same change.
- UC-11's **Postconditions** cell (line 648): change `An authentication token is issued` to `An authentication token is issued, and the response reports whether the person's email is verified`.
- UC-25's `else Google User exists` branch (around line 1246) ends with `DB-->>API: Confirmed`. Add the refresh after it, inside the same branch:

```
        API->>DB: Refresh EmailVerified from the token claims if it differs
```

- UC-25's numbered step 7 currently reads "If one exists: the system confirms it is not logically deleted." Change it to "If one exists: the system confirms it is not logically deleted, and refreshes the stored `EmailVerified` from the token's claims when the two differ (FR-GO-19)."

Read each mermaid block before editing: the arrow labels and participant aliases differ between use cases (`U`, `A`, `Caller`), so match the block you are editing rather than copying an arrow from another one. UC-25's blocks use `API` and `DB`; the indentation inside an `alt`/`else` branch is 8 spaces, not 4.

- [ ] **Step 5: Update the site docs**

- `docs/content/en/docs/getting-started.md:131`: change "The response carries `token` and `expiresAt`." to "The response carries `token`, `expiresAt`, and `emailVerified` — the last telling you whether to prompt the person to confirm their address."
- `docs/content/en/docs/flows/login.md:55,57`: change `DataOutput{token, expiresAt}` and `200 {token, expiresAt}` to include `, emailVerified`.
- `docs/content/en/docs/flows/google-sign-in.md:74` and `docs/content/en/docs/flows/two-factor.md:118`: change `200 {token, expiresAt}` to `200 {token, expiresAt, emailVerified}`.

- [ ] **Step 6: Regenerate the published OpenAPI document**

```bash
python scripts/openapi.py generate
```

Then confirm it matches what the built API serves:

```bash
python scripts/openapi.py check
```

Expected: check reports no drift. Confirm by eye that the diff adds `emailVerified` to exactly three response schemas and nothing else — if it touches request bodies or unrelated paths, stop and investigate rather than committing.

- [ ] **Step 7: Run the whole suite**

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit" 2>&1 | tail -20
```

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional" 2>&1 | tail -20
```

Expected: both PASS, `OpenApiContractTests` included.

- [ ] **Step 8: Commit**

```bash
git add docs
git commit -m "docs: document reported email verification status"
```

---

## Verification

Before opening the pull request:

- [ ] `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"` passes, output pasted rather than summarised.
- [ ] `dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional"` passes, likewise.
- [ ] `python scripts/openapi.py check` reports no drift.
- [ ] `git diff main --stat` shows changes only in the files this plan lists.
- [ ] A login response for an unverified person actually carries `"emailVerified": false` — grep the regenerated `docs/openapi/heimdall.json` for `emailVerified` and confirm three occurrences in response schemas.
