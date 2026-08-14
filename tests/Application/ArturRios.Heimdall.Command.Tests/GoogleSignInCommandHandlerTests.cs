using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for GoogleSignInCommandHandler (UC-25): both halves of the main flow — sign-up on a
// first visit (FR-GO-09) and sign-in on every later one (FR-GO-10) — the claims of the issued token
// (FR-GO-04), AF-25a (token fails verification), AF-25b (scope missing, deleted, or with the setting
// off), AF-25c (address taken in the scope by a Google User or a User person), and AF-25d (the Google
// User is logically deleted). The per-scope reach of the uniqueness rules (FR-GO-06/07/08) is pinned
// by the two "another scope" tests. The 401/403-by-attribute flows do not apply — the endpoint is
// anonymous — and the end-to-end behavior is covered by AuthControllerGoogleSignInTests.
public class GoogleSignInCommandHandlerTests
{
    private const string GoogleSubject = "google-sub-1234567890";
    private const string Email = "signer@gmail.test";

    private static readonly DateTime TokenExpiry = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    private static GoogleIdTokenPayload Payload(
        string subject = GoogleSubject,
        string email = Email,
        bool emailVerified = true,
        string? name = "Google Signer",
        string? pictureUrl = "https://lh3.googleusercontent.test/a/photo") =>
        new(subject, email, emailVerified, name, pictureUrl);

    /// <summary>A verifier that accepts every token and returns <paramref name="payload" />.</summary>
    private static IGoogleIdTokenVerifier Verifier(GoogleIdTokenPayload? payload)
    {
        var verifier = new Mock<IGoogleIdTokenVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<string>())).ReturnsAsync(payload);
        return verifier.Object;
    }

    /// <summary>An issuer that records the subject it was handed, so the claims can be asserted.</summary>
    private static (IAuthTokenIssuer issuer, Mock<IAuthTokenIssuer> mock) TokenIssuer()
    {
        var issuer = new Mock<IAuthTokenIssuer>();
        issuer
            .Setup(i => i.IssueAsync(It.IsAny<AuthTokenSubject>()))
            .ReturnsAsync(new AuthToken("issued-token", TokenExpiry));
        return (issuer.Object, issuer);
    }

    private static async Task<Scope> SeedScopeAsync(
        AsyncFakeRepository<Scope> scopes, bool googleSignInEnabled = true, bool isDeleted = false)
    {
        var scope = new Scope
        {
            PublicId = Guid.NewGuid(),
            Name = $"scope-{Guid.NewGuid():N}",
            GoogleSignInEnabled = googleSignInEnabled,
            IsDeleted = isDeleted
        };

        await scopes.CreateAsync(scope);

        return scope;
    }

    private static async Task<GoogleUser> SeedGoogleUserAsync(
        AsyncFakeRepository<GoogleUser> googleUsers,
        Scope scope,
        string googleId = GoogleSubject,
        string email = Email,
        bool isDeleted = false,
        bool emailVerified = true)
    {
        // Bogus fills the descriptive fields; only what the lookups read is pinned.
        var googleUser = new Bogus.Faker<GoogleUser>()
            .RuleFor(x => x.PublicId, _ => Guid.NewGuid())
            .RuleFor(x => x.GoogleId, _ => googleId)
            .RuleFor(x => x.Email, _ => email)
            .RuleFor(x => x.ScopeId, _ => scope.Id)
            .RuleFor(x => x.IsDeleted, _ => isDeleted)
            .RuleFor(x => x.EmailVerified, _ => emailVerified)
            .Generate();

        await googleUsers.CreateAsync(googleUser);

        return googleUser;
    }

    private static async Task SeedUserPersonAsync(
        AsyncFakeRepository<Person> persons, Scope scope, string email, bool isDeleted = false)
    {
        var person = new Bogus.Faker<Person>()
            .RuleFor(p => p.PublicId, _ => Guid.NewGuid())
            .RuleFor(p => p.Email, _ => email)
            .RuleFor(p => p.RoleId, _ => (long)Roles.User)
            .RuleFor(p => p.IsDeleted, _ => isDeleted)
            .RuleFor(p => p.ScopeMembership, _ => new ScopeUser { ScopeId = scope.Id })
            .Generate();

        await persons.CreateAsync(person);
    }

    private static GoogleSignInCommand Command(Guid scopeId) =>
        new() { ScopeId = scopeId, IdToken = "a-google-id-token" };

    [UnitFact]
    public async Task GivenNoExistingGoogleUser_WhenHandlingGoogleSignIn_ThenCreatesGoogleUserFromTokenClaimsAndIssuesToken()
    {
        // Given a scope with Google sign-in on and no Google User yet (UC-25 main flow, FR-GO-09)
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var (issuer, _) = TokenIssuer();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person>(), googleUsers, googleUsers, issuer);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then — output
        Assert.True(output.Success);
        Assert.Equal("issued-token", output.Data!.Token);
        Assert.Equal(TokenExpiry, output.Data.ExpiresAt);
        Assert.Contains(AuthMessages.GoogleSignInSuccessful, output.Messages);

        // Then — persisted state: every field comes from the verified token (FR-GO-05) and the row
        // is bound to the scope the sign-in named (FR-GO-06)
        var stored = (await googleUsers.GetAllAsync()).Data!.Single();
        Assert.Equal(GoogleSubject, stored.GoogleId);
        Assert.Equal(Email, stored.Email);
        Assert.Equal("Google Signer", stored.Name);
        Assert.True(stored.EmailVerified);
        Assert.Equal("https://lh3.googleusercontent.test/a/photo", stored.ProfilePictureUrl);
        Assert.Equal(scope.Id, stored.ScopeId);
        Assert.False(stored.IsDeleted);
    }

    [UnitFact]
    public async Task GivenExistingGoogleUser_WhenHandlingGoogleSignIn_ThenIssuesTokenWithoutCreatingDuplicate()
    {
        // Given the Google account already signed up in this scope (FR-GO-10)
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var existing = await SeedGoogleUserAsync(googleUsers, scope);
        var (issuer, issuerMock) = TokenIssuer();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person>(), googleUsers, googleUsers, issuer);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then — output
        Assert.True(output.Success);
        Assert.Contains(AuthMessages.GoogleSignInSuccessful, output.Messages);

        // Then — the existing row was reused, not duplicated
        var stored = (await googleUsers.GetAllAsync()).Data!.Single();
        Assert.Equal(existing.PublicId, stored.PublicId);
        issuerMock.Verify(i => i.IssueAsync(It.Is<AuthTokenSubject>(s => s.PersonId == existing.PublicId)), Times.Once);
    }

    [UnitFact]
    public async Task GivenSignUp_WhenHandlingGoogleSignIn_ThenIssuedTokenClaimsUserRoleAndScope()
    {
        // Given a first sign-in, so the token is issued for the row just created (UC-25 step 8,
        // FR-GO-04: Google authentication never yields a ScopeAdmin or SystemAdmin)
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var (issuer, issuerMock) = TokenIssuer();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person>(), googleUsers, googleUsers, issuer);

        // When
        await handler.HandleAsync(Command(scope.PublicId));

        // Then — the subject carries the Google User's PublicId (NFR-15), the User role, the one
        // scope it belongs to, and no owned scopes
        var created = (await googleUsers.GetAllAsync()).Data!.Single();
        issuerMock.Verify(
            i => i.IssueAsync(It.Is<AuthTokenSubject>(s =>
                s.PersonId == created.PublicId &&
                s.RoleId == (int)Roles.User &&
                s.ScopeId == scope.PublicId &&
                s.OwnedScopeIds.Count == 0)),
            Times.Once);
    }

    [UnitFact]
    public async Task GivenTokenFailsVerification_WhenHandlingGoogleSignIn_ThenReturnsAuthenticationFailedError()
    {
        // Given a token the verifier rejects — invalid, expired, or for another audience (AF-25a)
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(null), scopes, new AsyncFakeRepository<Person>(), googleUsers, googleUsers,
            TokenIssuer().issuer);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.GoogleAuthenticationFailed, output.Errors);
        Assert.Empty((await googleUsers.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenScopeDoesNotExist_WhenHandlingGoogleSignIn_ThenReturnsSignInUnavailableError()
    {
        // Given a scope identifier matching nothing (AF-25b)
        var scopes = new AsyncFakeRepository<Scope>();
        await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person>(), googleUsers, googleUsers,
            TokenIssuer().issuer);

        // When
        var output = await handler.HandleAsync(Command(Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.GoogleSignInUnavailable, output.Errors);
        Assert.Empty((await googleUsers.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenScopeIsLogicallyDeleted_WhenHandlingGoogleSignIn_ThenReturnsSignInUnavailableError()
    {
        // Given the scope is logically deleted (AF-25b, FR-GO-13)
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes, isDeleted: true);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person>(), googleUsers, googleUsers,
            TokenIssuer().issuer);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.GoogleSignInUnavailable, output.Errors);
    }

    [UnitFact]
    public async Task GivenScopeHasGoogleSignInDisabled_WhenHandlingGoogleSignIn_ThenReturnsSignInUnavailableError()
    {
        // Given the scope exists and is active but UC-24 left the setting off (AF-25b, FR-GO-03)
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes, googleSignInEnabled: false);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person>(), googleUsers, googleUsers,
            TokenIssuer().issuer);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.GoogleSignInUnavailable, output.Errors);
        Assert.Empty((await googleUsers.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenEmailBelongsToAnotherGoogleUserInScope_WhenHandlingGoogleSignIn_ThenReturnsEmailAlreadyExistsError()
    {
        // Given the address is already held in this scope by a Google User with a different 'sub'
        // (AF-25c, FR-GO-07)
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        await SeedGoogleUserAsync(googleUsers, scope, googleId: "another-google-sub");
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person>(), googleUsers, googleUsers,
            TokenIssuer().issuer);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then — refused, and no second row was written
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.EmailAlreadyExists, output.Errors);
        Assert.Single((await googleUsers.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenEmailBelongsToUserPersonInScope_WhenHandlingGoogleSignIn_ThenReturnsEmailAlreadyExistsError()
    {
        // Given the address belongs to a password-authenticated User of the same scope — the half of
        // FR-GO-07 no database index can enforce, since it spans two tables (AF-25c)
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var persons = new AsyncFakeRepository<Person>();
        await SeedUserPersonAsync(persons, scope, Email);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, persons, googleUsers, googleUsers, TokenIssuer().issuer);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.EmailAlreadyExists, output.Errors);
        Assert.Empty((await googleUsers.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenEmailDiffersOnlyByCase_WhenHandlingGoogleSignIn_ThenReturnsEmailAlreadyExistsError()
    {
        // Given the address is taken in a different case — uniqueness is case-insensitive, as it is
        // when a User is created (AF-25c)
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        await SeedGoogleUserAsync(googleUsers, scope, googleId: "another-google-sub", email: Email.ToUpper());
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person>(), googleUsers, googleUsers,
            TokenIssuer().issuer);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.EmailAlreadyExists, output.Errors);
    }

    [UnitFact]
    public async Task GivenSameEmailInAnotherScope_WhenHandlingGoogleSignIn_ThenCreatesGoogleUser()
    {
        // Given the address is held in a *different* scope — FR-GO-07 is scoped, so it does not
        // collide here
        var scopes = new AsyncFakeRepository<Scope>();
        var targetScope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        await SeedGoogleUserAsync(googleUsers, otherScope, googleId: "another-google-sub");
        var persons = new AsyncFakeRepository<Person>();
        await SeedUserPersonAsync(persons, otherScope, Email);
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, persons, googleUsers, googleUsers, TokenIssuer().issuer);

        // When
        var output = await handler.HandleAsync(Command(targetScope.PublicId));

        // Then
        Assert.True(output.Success);
        var stored = (await googleUsers.GetAllAsync()).Data!;
        Assert.Equal(2, stored.Count());
        Assert.Single(stored, x => x.ScopeId == targetScope.Id && x.Email == Email);
    }

    [UnitFact]
    public async Task GivenExistingGoogleUserIsLogicallyDeleted_WhenHandlingGoogleSignIn_ThenReturnsAuthenticationFailedError()
    {
        // Given the account signed up before and was logically deleted since (AF-25d, FR-GO-12)
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        await SeedGoogleUserAsync(googleUsers, scope, isDeleted: true);
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person>(), googleUsers, googleUsers,
            TokenIssuer().issuer);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then — refused with the same message an unverifiable token gets, and no row was revived or
        // re-created
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.GoogleAuthenticationFailed, output.Errors);
        Assert.Single((await googleUsers.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenSameGoogleIdInAnotherScope_WhenHandlingGoogleSignIn_ThenCreatesSeparateGoogleUser()
    {
        // Given the same Google account already signed up in a different scope — FR-GO-06/08 make a
        // Google User single-scope, so signing in to a second scope registers a second account
        var scopes = new AsyncFakeRepository<Scope>();
        var targetScope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var existing = await SeedGoogleUserAsync(googleUsers, otherScope);
        var (issuer, issuerMock) = TokenIssuer();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person>(), googleUsers, googleUsers, issuer);

        // When
        var output = await handler.HandleAsync(Command(targetScope.PublicId));

        // Then — a second row, and a token claiming the new one and the target scope
        Assert.True(output.Success);
        var stored = (await googleUsers.GetAllAsync()).Data!;
        Assert.Equal(2, stored.Count());
        var created = stored.Single(x => x.ScopeId == targetScope.Id);
        Assert.NotEqual(existing.PublicId, created.PublicId);
        issuerMock.Verify(
            i => i.IssueAsync(It.Is<AuthTokenSubject>(s =>
                s.PersonId == created.PublicId && s.ScopeId == targetScope.PublicId)),
            Times.Once);
    }

    [UnitFact]
    public async Task GivenTokenWithoutNameOrPicture_WhenHandlingGoogleSignIn_ThenCreatesGoogleUserWithEmptyName()
    {
        // Given a token whose issuer withheld the profile claims — the account is still created, with
        // the optional fields left empty (FR-GO-05)
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(name: null, pictureUrl: null)), scopes, new AsyncFakeRepository<Person>(),
            googleUsers, googleUsers, TokenIssuer().issuer);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.True(output.Success);
        var stored = (await googleUsers.GetAllAsync()).Data!.Single();
        Assert.Equal(string.Empty, stored.Name);
        Assert.Null(stored.ProfilePictureUrl);
    }

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
}
