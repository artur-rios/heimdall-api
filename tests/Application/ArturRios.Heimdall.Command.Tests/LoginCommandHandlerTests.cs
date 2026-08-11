using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Hashing;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for LoginCommandHandler (UC-11): the main flow for each of the three roles, AF-11a…
// AF-11e — every one of which must answer with the same InvalidCredentials error and issue no token
// — and AF-11g (a challenge token instead of a full one, for a person with active two-factor
// authentication). AF-11f is covered by LoginCommandValidatorTests; this class checks only that the
// handler stops when validation fails.
public class LoginCommandHandlerTests
{
    private const string Password = "Str0ngPass!";

    private static Scope Scope(long id, bool isDeleted = false) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"scope-{id}",
        IsDeleted = isDeleted
    };

    private static Person Person(long id, string email, Roles role, bool isDeleted = false,
        string password = Password) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"person-{id}",
        Email = email,
        PasswordHash = Hash.EncodeWithRandomSalt(password, out var salt),
        Salt = salt,
        RoleId = (long)role,
        IsDeleted = isDeleted
    };

    private static Person User(long id, string email, Scope scope, bool isDeleted = false,
        string password = Password)
    {
        var person = Person(id, email, Roles.User, isDeleted, password);
        person.ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope };
        return person;
    }

    private static Person ScopeAdmin(long id, string email, params Scope[] owned)
    {
        var person = Person(id, email, Roles.ScopeAdmin);
        person.ScopeOwnerships = owned
            .Select(scope => new ScopeOwner { ScopeId = scope.Id, Scope = scope })
            .ToList();
        return person;
    }

    private static async Task<AsyncFakeRepository<Person>> PersonsWith(params Person[] persons)
    {
        var repository = new AsyncFakeRepository<Person>();

        foreach (var person in persons)
        {
            await repository.CreateAsync(person);
        }

        return repository;
    }

    private static Mock<IValidator<LoginCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<LoginCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<LoginCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator;
    }

    /// <summary>
    ///     An issuer that records the subject it was handed, so the tests can assert on the claims
    ///     the token would carry (FR-AU-04) without decoding a JWT — that round trip belongs to
    ///     IdentityUserMapperTests.
    /// </summary>
    private sealed class RecordingIssuer : IAuthTokenIssuer
    {
        public AuthTokenSubject? Subject { get; private set; }

        public Task<AuthToken> IssueAsync(AuthTokenSubject subject)
        {
            Subject = subject;
            return Task.FromResult(
                new AuthToken("issued-token", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        }
    }

    /// <summary>Records whether AF-11g's challenge token was issued, and for whom, without a real JWT.</summary>
    private sealed class RecordingChallengeTokenIssuer : ITwoFactorChallengeTokenIssuer
    {
        public Guid? PersonId { get; private set; }
        public int? RoleId { get; private set; }

        public Task<AuthToken> IssueAsync(Guid personId, int roleId)
        {
            PersonId = personId;
            RoleId = roleId;
            return Task.FromResult(
                new AuthToken("challenge-token", new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc)));
        }
    }

    private sealed record Fixture(
        AsyncFakeRepository<Person> Persons,
        AsyncFakeRepository<TwoFactorAuth> TwoFactorAuths,
        AsyncFakeRepository<TwoFactorEmailCode> EmailCodes,
        RecordingIssuer TokenIssuer,
        RecordingChallengeTokenIssuer ChallengeTokenIssuer)
    {
        public LoginCommandHandler Handler() =>
            new(
                ValidValidator().Object,
                Persons,
                TwoFactorAuths,
                EmailCodes,
                EmailCodes,
                Mock.Of<ITwoFactorEmailSender>(),
                ChallengeTokenIssuer,
                new PersonAuthTokenService(TokenIssuer));
    }

    private static Fixture FixtureFor(AsyncFakeRepository<Person> persons) =>
        new(persons, new AsyncFakeRepository<TwoFactorAuth>(), new AsyncFakeRepository<TwoFactorEmailCode>(),
            new RecordingIssuer(), new RecordingChallengeTokenIssuer());

    private static LoginCommandHandler HandlerFor(
        AsyncFakeRepository<Person> persons, IAuthTokenIssuer issuer) =>
        new(
            ValidValidator().Object,
            persons,
            new AsyncFakeRepository<TwoFactorAuth>(),
            new AsyncFakeRepository<TwoFactorEmailCode>(),
            new AsyncFakeRepository<TwoFactorEmailCode>(),
            Mock.Of<ITwoFactorEmailSender>(),
            new RecordingChallengeTokenIssuer(),
            new PersonAuthTokenService(issuer));

    private static LoginCommand Command(string email, string password = Password, Guid? scopeId = null) =>
        new() { Email = email, Password = password, ScopeId = scopeId };

    [UnitFact]
    public async Task GivenUserWithMatchingScopeAndPassword_WhenHandlingLogin_ThenTokenIsIssuedWithScopeClaim()
    {
        // Given a User of a live scope
        var scope = Scope(1);
        var person = User(10, "user@test.local", scope);
        var persons = await PersonsWith(person);
        var issuer = new RecordingIssuer();

        // When
        var output = await HandlerFor(persons, issuer)
            .HandleAsync(Command("user@test.local", scopeId: scope.PublicId));

        // Then — output
        Assert.True(output.Success);
        Assert.Equal("issued-token", output.Data!.Token);
        Assert.False(output.Data.RequiresTwoFactor);
        Assert.Contains(AuthMessages.LoginSuccessful, output.Messages);

        // Then — claims: the person's and their scope's PublicIds, no owned scopes
        Assert.Equal(person.PublicId, issuer.Subject!.PersonId);
        Assert.Equal((int)Roles.User, issuer.Subject.RoleId);
        Assert.Equal(scope.PublicId, issuer.Subject.ScopeId);
        Assert.Empty(issuer.Subject.OwnedScopeIds);
    }

    [UnitFact]
    public async Task GivenScopeAdminOwningLiveScopes_WhenHandlingLogin_ThenTokenIsIssuedWithOwnedScopeClaims()
    {
        // Given a ScopeAdmin owning two live scopes, logging in without a scope id
        var first = Scope(1);
        var second = Scope(2);
        var person = ScopeAdmin(10, "admin@test.local", first, second);
        var persons = await PersonsWith(person);
        var issuer = new RecordingIssuer();

        // When
        var output = await HandlerFor(persons, issuer).HandleAsync(Command("admin@test.local"));

        // Then
        Assert.True(output.Success);
        Assert.Null(issuer.Subject!.ScopeId);
        Assert.Equal([first.PublicId, second.PublicId], issuer.Subject.OwnedScopeIds);
    }

    [UnitFact]
    public async Task GivenSystemAdmin_WhenHandlingLogin_ThenTokenIsIssuedWithNoScopeClaim()
    {
        // Given a SystemAdmin, who belongs to no scope
        var person = Person(10, "root@test.local", Roles.SystemAdmin);
        var persons = await PersonsWith(person);
        var issuer = new RecordingIssuer();

        // When
        var output = await HandlerFor(persons, issuer).HandleAsync(Command("root@test.local"));

        // Then
        Assert.True(output.Success);
        Assert.Equal(person.PublicId, issuer.Subject!.PersonId);
        Assert.Null(issuer.Subject.ScopeId);
        Assert.Empty(issuer.Subject.OwnedScopeIds);
    }

    [UnitFact]
    public async Task GivenEmailInDifferentCase_WhenHandlingLogin_ThenTokenIsIssued()
    {
        // Given the stored email differs only in case from the submitted one
        var person = Person(10, "Admin@Test.Local", Roles.SystemAdmin);
        var persons = await PersonsWith(person);
        var issuer = new RecordingIssuer();

        // When
        var output = await HandlerFor(persons, issuer).HandleAsync(Command("admin@test.local"));

        // Then
        Assert.True(output.Success);
    }

    [UnitFact]
    public async Task GivenScopeAdminWithOneLiveAndOneDeletedScope_WhenHandlingLogin_ThenOnlyLiveScopeIsClaimed()
    {
        // Given one owned scope deleted and one still live: FR-AU-07 admits the login, and the token
        // must not claim authority over the scope the system considers gone
        var live = Scope(1);
        var deleted = Scope(2, isDeleted: true);
        var person = ScopeAdmin(10, "admin@test.local", deleted, live);
        var persons = await PersonsWith(person);
        var issuer = new RecordingIssuer();

        // When
        var output = await HandlerFor(persons, issuer).HandleAsync(Command("admin@test.local"));

        // Then
        Assert.True(output.Success);
        Assert.Equal([live.PublicId], issuer.Subject!.OwnedScopeIds);
    }

    [UnitFact]
    public async Task GivenUnknownEmail_WhenHandlingLogin_ThenReturnsInvalidCredentials()
    {
        // Given — AF-11a
        var persons = await PersonsWith(Person(10, "admin@test.local", Roles.SystemAdmin));
        var issuer = new RecordingIssuer();

        // When
        var output = await HandlerFor(persons, issuer).HandleAsync(Command("nobody@test.local"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.InvalidCredentials, output.Errors);
        Assert.Null(issuer.Subject);
    }

    [UnitFact]
    public async Task GivenUserOfAnotherScope_WhenHandlingLogin_ThenReturnsInvalidCredentials()
    {
        // Given — AF-11a: the email exists, but as a User of a different scope. Two Users may share
        // an email across scopes, so the scope is part of the identity.
        var theirScope = Scope(1);
        var otherScope = Scope(2);
        var persons = await PersonsWith(User(10, "user@test.local", theirScope));
        var issuer = new RecordingIssuer();

        // When
        var output = await HandlerFor(persons, issuer)
            .HandleAsync(Command("user@test.local", scopeId: otherScope.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.InvalidCredentials, output.Errors);
        Assert.Null(issuer.Subject);
    }

    [UnitFact]
    public async Task GivenUserEmailWithoutScopeId_WhenHandlingLogin_ThenReturnsInvalidCredentials()
    {
        // Given — AF-11a: without a scope id the lookup is the admin one, which must not reach a User
        var scope = Scope(1);
        var persons = await PersonsWith(User(10, "user@test.local", scope));
        var issuer = new RecordingIssuer();

        // When
        var output = await HandlerFor(persons, issuer).HandleAsync(Command("user@test.local"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.InvalidCredentials, output.Errors);
        Assert.Null(issuer.Subject);
    }

    [UnitFact]
    public async Task GivenWrongPassword_WhenHandlingLogin_ThenReturnsInvalidCredentials()
    {
        // Given — AF-11b
        var persons = await PersonsWith(Person(10, "admin@test.local", Roles.SystemAdmin));
        var issuer = new RecordingIssuer();

        // When
        var output = await HandlerFor(persons, issuer)
            .HandleAsync(Command("admin@test.local", password: "Wr0ngPass!"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.InvalidCredentials, output.Errors);
        Assert.Null(issuer.Subject);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedPerson_WhenHandlingLogin_ThenReturnsInvalidCredentials()
    {
        // Given — AF-11c (FR-AU-05): correct credentials, deleted account
        var persons = await PersonsWith(
            Person(10, "admin@test.local", Roles.SystemAdmin, isDeleted: true));
        var issuer = new RecordingIssuer();

        // When
        var output = await HandlerFor(persons, issuer).HandleAsync(Command("admin@test.local"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.InvalidCredentials, output.Errors);
        Assert.Null(issuer.Subject);
    }

    [UnitFact]
    public async Task GivenUserWhoseScopeIsDeleted_WhenHandlingLogin_ThenReturnsInvalidCredentials()
    {
        // Given — AF-11d (FR-AU-06)
        var scope = Scope(1, isDeleted: true);
        var persons = await PersonsWith(User(10, "user@test.local", scope));
        var issuer = new RecordingIssuer();

        // When
        var output = await HandlerFor(persons, issuer)
            .HandleAsync(Command("user@test.local", scopeId: scope.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.InvalidCredentials, output.Errors);
        Assert.Null(issuer.Subject);
    }

    [UnitFact]
    public async Task GivenScopeAdminWhoseScopesAreAllDeleted_WhenHandlingLogin_ThenReturnsInvalidCredentials()
    {
        // Given — AF-11e (FR-AU-07)
        var persons = await PersonsWith(ScopeAdmin(
            10, "admin@test.local", Scope(1, isDeleted: true), Scope(2, isDeleted: true)));
        var issuer = new RecordingIssuer();

        // When
        var output = await HandlerFor(persons, issuer).HandleAsync(Command("admin@test.local"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.InvalidCredentials, output.Errors);
        Assert.Null(issuer.Subject);
    }

    [UnitFact]
    public async Task GivenInvalidInput_WhenHandlingLogin_ThenReturnsValidationErrorAndIssuesNoToken()
    {
        // Given — AF-11f: the validator rejects the command, so no lookup should happen
        var persons = await PersonsWith(Person(10, "admin@test.local", Roles.SystemAdmin));
        var issuer = new RecordingIssuer();
        var validator = new Mock<IValidator<LoginCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<LoginCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([
                new ValidationFailure(nameof(LoginCommand.Email), AuthMessages.EmailRequired)
            ]));
        var handler = new LoginCommandHandler(
            validator.Object,
            persons,
            new AsyncFakeRepository<TwoFactorAuth>(),
            new AsyncFakeRepository<TwoFactorEmailCode>(),
            new AsyncFakeRepository<TwoFactorEmailCode>(),
            Mock.Of<ITwoFactorEmailSender>(),
            new RecordingChallengeTokenIssuer(),
            new PersonAuthTokenService(issuer));

        // When
        var output = await handler.HandleAsync(Command("admin@test.local"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.EmailRequired, output.Errors);
        Assert.Null(issuer.Subject);
    }

    [UnitFact]
    public async Task GivenPersonWithActiveTwoFactorAuth_WhenHandlingLogin_ThenChallengeTokenIsIssuedInsteadOfFullToken()
    {
        // Given — AF-11g (FR-2F-07): correct credentials, but active 2FA
        var person = Person(10, "admin@test.local", Roles.SystemAdmin);
        var fixture = FixtureFor(await PersonsWith(person));
        await fixture.TwoFactorAuths.CreateAsync(new TwoFactorAuth
        {
            PersonId = person.Id, IsActive = true, AppEnabled = true, EmailEnabled = true
        });

        // When
        var output = await fixture.Handler().HandleAsync(Command("admin@test.local"));

        // Then — a challenge token, not a full one
        Assert.True(output.Success);
        Assert.True(output.Data!.RequiresTwoFactor);
        Assert.Equal("challenge-token", output.Data.ChallengeToken);
        Assert.Null(output.Data.Token);
        Assert.Null(output.Data.ExpiresAt);
        Assert.Equal(["App", "Email"], output.Data.AvailableMethods);
        Assert.Contains(AuthMessages.TwoFactorRequired, output.Messages);

        // Then — no full token was ever built, and the challenge named the right person
        Assert.Null(fixture.TokenIssuer.Subject);
        Assert.Equal(person.PublicId, fixture.ChallengeTokenIssuer.PersonId);
        Assert.Equal((int)Roles.SystemAdmin, fixture.ChallengeTokenIssuer.RoleId);
    }

    [UnitFact]
    public async Task GivenPersonWithOnlyAppTwoFactorEnabled_WhenHandlingLogin_ThenAvailableMethodsListsAppOnly()
    {
        // Given — AF-11g, App method only
        var person = Person(10, "app-only@test.local", Roles.SystemAdmin);
        var fixture = FixtureFor(await PersonsWith(person));
        await fixture.TwoFactorAuths.CreateAsync(new TwoFactorAuth
        {
            PersonId = person.Id, IsActive = true, AppEnabled = true, EmailEnabled = false
        });

        // When
        var output = await fixture.Handler().HandleAsync(Command("app-only@test.local"));

        // Then
        Assert.True(output.Success);
        Assert.Equal(["App"], output.Data!.AvailableMethods);
        Assert.Empty(fixture.EmailCodes.Query().ToList());
    }

    [UnitFact]
    public async Task GivenPersonWithEmailTwoFactorEnabled_WhenHandlingLogin_ThenFreshEmailCodeIsIssued()
    {
        // Given — AF-11g/FR-2F-08: the Email method is enabled, so a fresh code must be mailed
        var person = Person(10, "email-2fa@test.local", Roles.SystemAdmin);
        var fixture = FixtureFor(await PersonsWith(person));
        var twoFactorAuth = new TwoFactorAuth
        {
            PersonId = person.Id, IsActive = true, AppEnabled = false, EmailEnabled = true
        };
        await fixture.TwoFactorAuths.CreateAsync(twoFactorAuth);
        var stale = new TwoFactorEmailCode
        {
            TwoFactorAuthId = twoFactorAuth.Id,
            CodeHash = [9, 9, 9],
            Salt = [1, 1, 1],
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            Used = false
        };
        await fixture.EmailCodes.CreateAsync(stale);

        // When
        var output = await fixture.Handler().HandleAsync(Command("email-2fa@test.local"));

        // Then — the prior outstanding code was retired and a fresh one issued
        Assert.True(output.Success);
        Assert.True(stale.Used);
        Assert.Equal(2, fixture.EmailCodes.Query().ToList().Count);
        Assert.Single(fixture.EmailCodes.Query().ToList(), code => !code.Used);
    }

    [UnitFact]
    public async Task GivenPersonWithNoTwoFactorAuthRow_WhenHandlingLogin_ThenFullTokenIsIssuedAsBefore()
    {
        // Given — regression: a person with no TwoFactorAuth row at all must log in exactly as before
        // UC-38 existed.
        var person = Person(10, "no-2fa@test.local", Roles.SystemAdmin);
        var fixture = FixtureFor(await PersonsWith(person));

        // When
        var output = await fixture.Handler().HandleAsync(Command("no-2fa@test.local"));

        // Then
        Assert.True(output.Success);
        Assert.False(output.Data!.RequiresTwoFactor);
        Assert.Equal("issued-token", output.Data.Token);
        Assert.Null(output.Data.ChallengeToken);
        Assert.Null(output.Data.AvailableMethods);
        Assert.Contains(AuthMessages.LoginSuccessful, output.Messages);
        Assert.Null(fixture.ChallengeTokenIssuer.PersonId);
    }

    [UnitFact]
    public async Task GivenPersonWithInactiveTwoFactorAuthRow_WhenHandlingLogin_ThenFullTokenIsIssued()
    {
        // Given — a pending (never confirmed) UC-36/UC-37 setup must not gate login: only
        // IsActive == true does (FR-2F-07)
        var person = Person(10, "pending-2fa@test.local", Roles.SystemAdmin);
        var fixture = FixtureFor(await PersonsWith(person));
        await fixture.TwoFactorAuths.CreateAsync(new TwoFactorAuth
        {
            PersonId = person.Id, IsActive = false, AppEnabled = true
        });

        // When
        var output = await fixture.Handler().HandleAsync(Command("pending-2fa@test.local"));

        // Then
        Assert.True(output.Success);
        Assert.False(output.Data!.RequiresTwoFactor);
        Assert.Equal("issued-token", output.Data.Token);
    }
}
