using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Command.Tests.Support;
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
// off), AF-25c (address taken in the scope by a Google User or a User person), AF-25d (the Google
// User is logically deleted), AF-25f (a sign-up whose address Google does not vouch for) and AF-25g
// (a nonce the token does not carry). The per-scope reach of the uniqueness rules (FR-GO-06/07/08) is pinned
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
        bool? emailVerified = true,
        string? name = "Google Signer",
        string? pictureUrl = "https://lh3.googleusercontent.test/a/photo",
        string? tokenId = "jti-1") =>
        new(subject, email, emailVerified, name, pictureUrl, TokenId: tokenId, ExpiresAt: TokenExpiry);

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
        AsyncFakeRepository<Scope, long> scopes, bool googleSignInEnabled = true, bool isDeleted = false)
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
        AsyncFakeRepository<GoogleUser, long> googleUsers,
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
        AsyncFakeRepository<Person, long> persons, Scope scope, string email, bool isDeleted = false)
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
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var (issuer, _) = TokenIssuer();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person, long>(), googleUsers, googleUsers, issuer,
            InMemoryAtomicWrites.Instance);

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
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var existing = await SeedGoogleUserAsync(googleUsers, scope);
        var (issuer, issuerMock) = TokenIssuer();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person, long>(), googleUsers, googleUsers, issuer,
            InMemoryAtomicWrites.Instance);

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
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var (issuer, issuerMock) = TokenIssuer();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person, long>(), googleUsers, googleUsers, issuer,
            InMemoryAtomicWrites.Instance);

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
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(null), scopes, new AsyncFakeRepository<Person, long>(), googleUsers, googleUsers,
            TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

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
        var scopes = new AsyncFakeRepository<Scope, long>();
        await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person, long>(), googleUsers, googleUsers,
            TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

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
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes, isDeleted: true);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person, long>(), googleUsers, googleUsers,
            TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

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
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes, googleSignInEnabled: false);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person, long>(), googleUsers, googleUsers,
            TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

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
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        await SeedGoogleUserAsync(googleUsers, scope, googleId: "another-google-sub");
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person, long>(), googleUsers, googleUsers,
            TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

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
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var persons = new AsyncFakeRepository<Person, long>();
        await SeedUserPersonAsync(persons, scope, Email);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, persons, googleUsers, googleUsers, TokenIssuer().issuer,
            InMemoryAtomicWrites.Instance);

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
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        await SeedGoogleUserAsync(googleUsers, scope, googleId: "another-google-sub", email: Email.ToUpper());
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person, long>(), googleUsers, googleUsers,
            TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

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
        var scopes = new AsyncFakeRepository<Scope, long>();
        var targetScope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        await SeedGoogleUserAsync(googleUsers, otherScope, googleId: "another-google-sub");
        var persons = new AsyncFakeRepository<Person, long>();
        await SeedUserPersonAsync(persons, otherScope, Email);
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, persons, googleUsers, googleUsers, TokenIssuer().issuer,
            InMemoryAtomicWrites.Instance);

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
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        await SeedGoogleUserAsync(googleUsers, scope, isDeleted: true);
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person, long>(), googleUsers, googleUsers,
            TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then — refused with the same message an unverifiable token gets, and no row was revived or
        // re-created
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.GoogleAuthenticationFailed, output.Errors);
        Assert.Single((await googleUsers.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenExistingGoogleUserIsRestricted_WhenHandlingGoogleSignIn_ThenOnlyASubjectRightsTokenIsIssued()
    {
        // Given the account's processing is restricted (NFR-24), and the stored flag disagrees with
        // the token — which must not be written back, since that would be processing the record
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var googleUser = await SeedGoogleUserAsync(googleUsers, scope, emailVerified: false);
        googleUser.ProcessingRestrictedAt = DateTime.UtcNow;
        var (issuer, issuerMock) = TokenIssuer();
        issuerMock
            .Setup(i => i.IssueSubjectRightsOnlyAsync(googleUser.PublicId, (int)Roles.User, null))
            .ReturnsAsync(new AuthToken("rights-only-token", TokenExpiry));
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(emailVerified: true)), scopes, new AsyncFakeRepository<Person, long>(), googleUsers,
            googleUsers, issuer, InMemoryAtomicWrites.Instance);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then — a subject-rights-only token, never a full one, and the row untouched
        Assert.True(output.Success);
        Assert.Equal("rights-only-token", output.Data!.Token);
        Assert.True(output.Data.ProcessingRestricted);
        Assert.Contains(AuthMessages.SubjectRightsOnlySignIn, output.Messages);
        issuerMock.Verify(i => i.IssueAsync(It.IsAny<AuthTokenSubject>()), Times.Never);
        Assert.False(googleUser.EmailVerified);
    }

    [UnitFact]
    public async Task GivenExistingGoogleUserIsRestrictedAndSuspended_WhenHandlingGoogleSignIn_ThenOnlyASubjectRightsTokenIsIssued()
    {
        // Given — a suspended subject keeps their rights until anonymised (UC-41, UC-44)
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var googleUser = await SeedGoogleUserAsync(googleUsers, scope, isDeleted: true);
        googleUser.ProcessingRestrictedAt = DateTime.UtcNow;
        var (issuer, issuerMock) = TokenIssuer();
        issuerMock
            .Setup(i => i.IssueSubjectRightsOnlyAsync(googleUser.PublicId, (int)Roles.User, null))
            .ReturnsAsync(new AuthToken("rights-only-token", TokenExpiry));
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person, long>(), googleUsers, googleUsers, issuer,
            InMemoryAtomicWrites.Instance);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.ProcessingRestricted);
        issuerMock.Verify(i => i.IssueAsync(It.IsAny<AuthTokenSubject>()), Times.Never);
    }

    [UnitFact]
    public async Task GivenExistingGoogleUserIsAnonymised_WhenHandlingGoogleSignIn_ThenReturnsAuthenticationFailedError()
    {
        // Given — NFR-20: nobody's any more
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var googleUser = await SeedGoogleUserAsync(googleUsers, scope, isDeleted: true);
        googleUser.AnonymisedAt = DateTime.UtcNow;
        googleUser.ProcessingRestrictedAt = DateTime.UtcNow;
        var (issuer, issuerMock) = TokenIssuer();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person, long>(), googleUsers, googleUsers, issuer,
            InMemoryAtomicWrites.Instance);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.GoogleAuthenticationFailed, output.Errors);
        issuerMock.Verify(i => i.IssueSubjectRightsOnlyAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<Guid?>()), Times.Never);
    }

    [UnitFact]
    public async Task GivenSameGoogleIdInAnotherScope_WhenHandlingGoogleSignIn_ThenCreatesSeparateGoogleUser()
    {
        // Given the same Google account already signed up in a different scope — FR-GO-06/08 make a
        // Google User single-scope, so signing in to a second scope registers a second account
        var scopes = new AsyncFakeRepository<Scope, long>();
        var targetScope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var existing = await SeedGoogleUserAsync(googleUsers, otherScope);
        var (issuer, issuerMock) = TokenIssuer();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person, long>(), googleUsers, googleUsers, issuer,
            InMemoryAtomicWrites.Instance);

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
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(name: null, pictureUrl: null)), scopes, new AsyncFakeRepository<Person, long>(),
            googleUsers, googleUsers, TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

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
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(emailVerified: true)), scopes, new AsyncFakeRepository<Person, long>(),
            googleUsers, googleUsers, TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.EmailVerified);
    }

    [UnitFact]
    public async Task GivenPayloadReportsUnverifiedAddress_WhenAnExistingGoogleUserSignsIn_ThenOutputReportsEmailVerifiedFalse()
    {
        // Given Google asserting the address is not verified — email_verified can be false — for a
        // Google User that already exists: sign-in is not gated on the claim, only sign-up is
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        await SeedGoogleUserAsync(googleUsers, scope, emailVerified: false);
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(emailVerified: false)), scopes, new AsyncFakeRepository<Person, long>(),
            googleUsers, googleUsers, TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

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
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        await SeedGoogleUserAsync(googleUsers, scope, emailVerified: false);
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(emailVerified: true)), scopes, new AsyncFakeRepository<Person, long>(),
            googleUsers, googleUsers, TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

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
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        await SeedGoogleUserAsync(googleUsers, scope, emailVerified: false);
        // A mock writer, not the fake repository: the fake holds entity references, so the handler's
        // in-memory assignment alone would satisfy a GetAllAsync() assertion whether or not the row
        // was ever written back. Verifying the write is the only way this test can fail if the
        // UpdateAsync call goes away.
        var writer = new Mock<IAsyncRepository<GoogleUser, long>>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(emailVerified: true)), scopes, new AsyncFakeRepository<Person, long>(),
            googleUsers, writer.Object, TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then — the sign-in succeeded and the row was written back carrying Google's value
        Assert.True(output.Success);
        writer.Verify(w => w.UpdateAsync(It.Is<GoogleUser>(g => g.EmailVerified)), Times.Once);
    }

    [UnitFact]
    public async Task GivenGoogleRevokedVerification_WhenHandlingGoogleSignIn_ThenStoredValueIsRefreshedToFalse()
    {
        // Given the refresh running in the other direction too — the rule is "match the token",
        // not "only ever turn the flag on"
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        await SeedGoogleUserAsync(googleUsers, scope, emailVerified: true);
        var writer = new Mock<IAsyncRepository<GoogleUser, long>>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(emailVerified: false)), scopes, new AsyncFakeRepository<Person, long>(),
            googleUsers, writer.Object, TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then — written back, in the other direction
        Assert.True(output.Success);
        writer.Verify(w => w.UpdateAsync(It.Is<GoogleUser>(g => !g.EmailVerified)), Times.Once);
    }

    [UnitFact]
    public async Task GivenStoredValueAlreadyAgrees_WhenHandlingGoogleSignIn_ThenNoUpdateIsWritten()
    {
        // Given a row already matching the token: the ordinary sign-in path stays read-only
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        await SeedGoogleUserAsync(googleUsers, scope, emailVerified: true);
        var writer = new Mock<IAsyncRepository<GoogleUser, long>>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(emailVerified: true)), scopes, new AsyncFakeRepository<Person, long>(),
            googleUsers, writer.Object, TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.True(output.Success);
        writer.Verify(w => w.UpdateAsync(It.IsAny<GoogleUser>()), Times.Never);
    }

    [UnitFact]
    public async Task GivenTokenOmitsEmailVerifiedClaim_WhenHandlingGoogleSignIn_ThenStoredVerifiedValueIsKeptAndReported()
    {
        // Given a returning Google User stored as verified whose client obtained a token without the
        // email scope, so it carries no email_verified claim at all: silence is not an assertion
        // that the address is unverified, so FR-GO-19's refresh must leave the row alone
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        await SeedGoogleUserAsync(googleUsers, scope, emailVerified: true);
        var writer = new Mock<IAsyncRepository<GoogleUser, long>>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(emailVerified: null)), scopes, new AsyncFakeRepository<Person, long>(),
            googleUsers, writer.Object, TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then — nothing written, and the caller is told what is stored rather than a downgrade
        Assert.True(output.Success);
        Assert.True(output.Data!.EmailVerified);
        writer.Verify(w => w.UpdateAsync(It.IsAny<GoogleUser>()), Times.Never);
        var stored = (await googleUsers.GetAllAsync()).Data!.Single();
        Assert.True(stored.EmailVerified);
    }

    // AF-25a: a first sign-up needs an email claim to create the account from.

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GivenASignUpTokenWithoutAnEmailClaim_WhenHandlingGoogleSignIn_ThenAuthenticationFailedAndNothingIsCreated(
        string? email)
    {
        // Given a first sign-in whose token carries no usable email, as when it was issued without the scope
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var (issuer, issuerMock) = TokenIssuer();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(email: email!)), scopes, new AsyncFakeRepository<Person, long>(),
            googleUsers, googleUsers, issuer, InMemoryAtomicWrites.Instance);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then AF-25a's answer, not an unhandled exception
        Assert.False(output.Success);
        Assert.Equal([AuthMessages.GoogleAuthenticationFailed], output.Errors);
        Assert.Empty((await googleUsers.GetAllAsync()).Data!);
        issuerMock.Verify(i => i.IssueAsync(It.IsAny<AuthTokenSubject>()), Times.Never);
    }

    // AF-25f (FR-GO-07): a sign-up needs an address Google vouches for.

    [UnitTheory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task GivenASignUpTokenWhoseAddressIsNotVerified_WhenHandlingGoogleSignIn_ThenRefusedAndNothingIsCreated(
        bool? emailVerified)
    {
        // Given a first sign-in with email_verified false, or absent — absence proves nothing
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var (issuer, issuerMock) = TokenIssuer();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(emailVerified: emailVerified)), scopes, new AsyncFakeRepository<Person, long>(),
            googleUsers, googleUsers, issuer, InMemoryAtomicWrites.Instance);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Equal([AuthMessages.GoogleEmailNotVerified], output.Errors);
        Assert.Empty((await googleUsers.GetAllAsync()).Data!);
        issuerMock.Verify(i => i.IssueAsync(It.IsAny<AuthTokenSubject>()), Times.Never);
    }

    [UnitFact]
    public async Task GivenAnUnverifiedSignUpTokenForATakenAddress_WhenHandlingGoogleSignIn_ThenItIsNotToldTheAddressIsTaken()
    {
        // Given the address held by a User person of the scope, and a token Google does not vouch for
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var persons = new AsyncFakeRepository<Person, long>();
        await SeedUserPersonAsync(persons, scope, Email);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(emailVerified: false)), scopes, persons, googleUsers, googleUsers,
            TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then — the same answer a free address gets, not AF-25c's
        Assert.Equal([AuthMessages.GoogleEmailNotVerified], output.Errors);
    }

    [UnitFact]
    public async Task GivenAnExistingGoogleUserAndATokenWithoutTheClaim_WhenHandlingGoogleSignIn_ThenSignedIn()
    {
        // Given — FR-GO-07's gate is on creating a Google User, not on signing one in
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        await SeedGoogleUserAsync(googleUsers, scope, emailVerified: false);
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(emailVerified: null)), scopes, new AsyncFakeRepository<Person, long>(),
            googleUsers, googleUsers, TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Contains(AuthMessages.GoogleSignInSuccessful, output.Messages);
    }

    // AF-25g (FR-GO-20): a nonce the caller sends binds the token to this sign-in.

    private static GoogleSignInCommand Command(Guid scopeId, string? nonce) =>
        new() { ScopeId = scopeId, IdToken = "a-google-id-token", Nonce = nonce };

    [UnitFact]
    public async Task GivenANonceTheTokenCarries_WhenHandlingGoogleSignIn_ThenSignedIn()
    {
        // Given
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload() with { Nonce = "n-0123456789" }), scopes, new AsyncFakeRepository<Person, long>(),
            googleUsers, googleUsers, TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, nonce: "n-0123456789"));

        // Then
        Assert.True(output.Success);
    }

    [UnitTheory]
    [InlineData("n-other")]
    [InlineData("N-0123456789")]
    [InlineData(null)]
    public async Task GivenANonceTheTokenDoesNotCarry_WhenHandlingGoogleSignIn_ThenRefusedAsAnUnverifiableToken(
        string? tokenNonce)
    {
        // Given a different nonce, one differing only in case, or none at all on the token
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        await SeedGoogleUserAsync(googleUsers, scope);
        var (issuer, issuerMock) = TokenIssuer();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload() with { Nonce = tokenNonce }), scopes, new AsyncFakeRepository<Person, long>(),
            googleUsers, googleUsers, issuer, InMemoryAtomicWrites.Instance);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, nonce: "n-0123456789"));

        // Then
        Assert.False(output.Success);
        Assert.Equal([AuthMessages.GoogleAuthenticationFailed], output.Errors);
        issuerMock.Verify(i => i.IssueAsync(It.IsAny<AuthTokenSubject>()), Times.Never);
    }

    [UnitFact]
    public async Task GivenAWrongNonceForAScopeWithoutGoogleSignIn_WhenHandlingGoogleSignIn_ThenTheTokenIsRefusedFirst()
    {
        // Given — like AF-25a, the nonce is part of the token check, made before the scope is read
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes, googleSignInEnabled: false);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload() with { Nonce = "n-other" }), scopes, new AsyncFakeRepository<Person, long>(),
            googleUsers, googleUsers, TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, nonce: "n-0123456789"));

        // Then
        Assert.Equal([AuthMessages.GoogleAuthenticationFailed], output.Errors);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    public async Task GivenNoNonce_WhenTheTokenCarriesOne_ThenNothingIsComparedAndSignedIn(string? nonce)
    {
        // Given a request that sends no nonce, or an empty one: behaviour as before FR-GO-20
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = await SeedScopeAsync(scopes);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload() with { Nonce = "n-0123456789" }), scopes, new AsyncFakeRepository<Person, long>(),
            googleUsers, googleUsers, TokenIssuer().issuer, InMemoryAtomicWrites.Instance);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, nonce));

        // Then
        Assert.True(output.Success);
    }

    [UnitFact]
    public async Task GivenATokenAlreadyUsed_WhenSigningInAgain_ThenRefusedAsAnInvalidTokenAndNothingIsCreated()
    {
        // Given (FR-GO-21) a token that has signed somebody up once
        var scopes = new AsyncFakeRepository<Scope, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var scope = await SeedScopeAsync(scopes);
        var (issuer, issuerMock) = TokenIssuer();
        var atomicWrites = new InMemoryAtomicWrites { GoogleIdTokenUses = new HashSet<string>() };
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person, long>(), googleUsers, googleUsers, issuer,
            atomicWrites);
        Assert.True((await handler.HandleAsync(Command(scope.PublicId))).Success);
        var created = (await googleUsers.GetAllAsync()).Data!.Count();
        issuerMock.Invocations.Clear();

        // When the same token is presented again
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Equal([AuthMessages.GoogleAuthenticationFailed], output.Errors);
        Assert.Equal(created, (await googleUsers.GetAllAsync()).Data!.Count());
        issuerMock.Verify(i => i.IssueAsync(It.IsAny<AuthTokenSubject>()), Times.Never);
    }

    [UnitFact]
    public async Task GivenTwoTokensSharingAnId_WhenBothArePresented_ThenTheSecondIsRefused()
    {
        // Given two different token strings that carry the same jti: the jti is what identifies a token
        var scopes = new AsyncFakeRepository<Scope, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var scope = await SeedScopeAsync(scopes);
        var atomicWrites = new InMemoryAtomicWrites { GoogleIdTokenUses = new HashSet<string>() };
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(tokenId: "shared-jti")), scopes, new AsyncFakeRepository<Person, long>(), googleUsers,
            googleUsers, TokenIssuer().issuer, atomicWrites);

        // When
        var first = await handler.HandleAsync(new GoogleSignInCommand { ScopeId = scope.PublicId, IdToken = "token-a" });
        var second = await handler.HandleAsync(new GoogleSignInCommand { ScopeId = scope.PublicId, IdToken = "token-b" });

        // Then
        Assert.True(first.Success);
        Assert.Equal([AuthMessages.GoogleAuthenticationFailed], second.Errors);
    }

    [UnitFact]
    public async Task GivenTokensWithoutAnId_WhenPresented_ThenEachIsKnownByItsSignedPart()
    {
        // Given tokens carrying no jti, which fall back to a digest of their signed part
        var scopes = new AsyncFakeRepository<Scope, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var scope = await SeedScopeAsync(scopes);
        var atomicWrites = new InMemoryAtomicWrites { GoogleIdTokenUses = new HashSet<string>() };
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload(tokenId: null)), scopes, new AsyncFakeRepository<Person, long>(), googleUsers,
            googleUsers, TokenIssuer().issuer, atomicWrites);

        // When
        var first = await handler.HandleAsync(
            new GoogleSignInCommand { ScopeId = scope.PublicId, IdToken = "header.payload-a.signature" });
        var other = await handler.HandleAsync(
            new GoogleSignInCommand { ScopeId = scope.PublicId, IdToken = "header.payload-b.signature" });
        var replay = await handler.HandleAsync(
            new GoogleSignInCommand { ScopeId = scope.PublicId, IdToken = "header.payload-a.signaturf" });

        // Then — the replay differs only in its signature segment, and is refused
        Assert.True(first.Success);
        Assert.True(other.Success);
        Assert.Equal([AuthMessages.GoogleAuthenticationFailed], replay.Errors);
        Assert.Contains(GoogleIdTokenUse.KeyOf(null, "header.payload-a.signature"), atomicWrites.GoogleIdTokenUses);
    }

    [UnitFact]
    public async Task GivenANonceMismatch_WhenSigningIn_ThenTheTokenIsNotSpent()
    {
        // Given a token refused for another sign-in attempt's nonce (AF-25g)
        var scopes = new AsyncFakeRepository<Scope, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var scope = await SeedScopeAsync(scopes);
        var atomicWrites = new InMemoryAtomicWrites { GoogleIdTokenUses = new HashSet<string>() };
        var handler = new GoogleSignInCommandHandler(
            Verifier(Payload()), scopes, new AsyncFakeRepository<Person, long>(), googleUsers, googleUsers,
            TokenIssuer().issuer, atomicWrites);

        // When
        var output = await handler.HandleAsync(
            new GoogleSignInCommand { ScopeId = scope.PublicId, IdToken = "a-google-id-token", Nonce = "other" });

        // Then nothing is recorded: only an accepted token is spent
        Assert.False(output.Success);
        Assert.Empty(atomicWrites.GoogleIdTokenUses!);
    }
}
