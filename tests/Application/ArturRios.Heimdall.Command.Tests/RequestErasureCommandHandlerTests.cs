using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for RequestErasureCommandHandler (UC-42). Three properties matter more than the rest:
// that the subject is always the caller, that a bearer token alone cannot trigger an irreversible
// operation, and that a request NFR-12 blocks is still recorded with its deadline running rather
// than refused.
public class RequestErasureCommandHandlerTests
{
    private const string Password = "Str0ng-Pass!";
    private const string WrongPassword = "not-the-password";
    private const string GoogleSubject = "108124442542040415000";

    private static readonly TimeSpan ErasureDeadline = TimeSpan.FromDays(30);

    private static DataRetentionOptions Retention() => new() { SubjectErasureDeadline = ErasureDeadline };

    private static IGoogleIdTokenVerifier Verifier(GoogleIdTokenPayload? payload)
    {
        var verifier = new Mock<IGoogleIdTokenVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<string>())).ReturnsAsync(payload);
        return verifier.Object;
    }

    private static GoogleIdTokenPayload Payload(string subject = GoogleSubject) =>
        new(subject, "ada@test.local", true, "Ada", null);

    private static RequestErasureCommandHandler Handler(
        AsyncFakeRepository<Person> persons,
        AsyncFakeRepository<GoogleUser> googleUsers,
        GoogleIdTokenPayload? payload = null) =>
        new(new RequestErasureCommandValidator(),
            persons, persons, googleUsers, googleUsers,
            Verifier(payload), Retention());

    private static async Task<Person> SeedPersonAsync(
        AsyncFakeRepository<Person> persons,
        Roles role = Roles.User,
        DateTime? erasureRequestedAt = null,
        params long[] ownedScopeIds)
    {
        var (hash, salt) = await PasswordHashGate.Shared.EncodeWithRandomSaltAsync(Password);

        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Ada Lovelace",
            Email = $"ada-{Guid.NewGuid():N}@test.local",
            PasswordHash = hash,
            Salt = salt,
            RoleId = (long)role,
            ErasureRequestedAt = erasureRequestedAt,
            ScopeOwnerships = ownedScopeIds
                .Select(scopeId => new ScopeOwner { ScopeId = scopeId })
                .ToList()
        };

        await persons.CreateAsync(person);

        return person;
    }

    private static async Task<GoogleUser> SeedGoogleUserAsync(AsyncFakeRepository<GoogleUser> googleUsers)
    {
        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = GoogleSubject,
            Name = "Ada Lovelace",
            Email = $"ada-{Guid.NewGuid():N}@test.local",
            ScopeId = 1
        };

        await googleUsers.CreateAsync(googleUser);

        return googleUser;
    }

    private static RequestErasureCommand Command(Guid actingPersonId, string? password = Password, string? idToken = null) =>
        new() { ActingPersonId = actingPersonId, ActingRole = (int)Roles.User, Password = password, IdToken = idToken };

    [UnitFact]
    public async Task GivenAPersonWithTheirPassword_WhenRequestingErasure_ThenItIsRecordedAndTheyAreSuspended()
    {
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var person = await SeedPersonAsync(persons);

        var output = await Handler(persons, googleUsers).HandleAsync(Command(person.PublicId));

        Assert.True(output.Success);
        Assert.Equal(ErasureMessages.ErasureRequested, output.Messages.First());
        Assert.Equal(person.PublicId, output.Data!.Id);
        Assert.False(output.Data.Blocked);

        // The deadline is returned because Art. 12(3) is an obligation owed to the subject
        Assert.Equal(output.Data.RequestedAt + ErasureDeadline, output.Data.DueAt);

        // Suspended immediately: a subject who has asked to be erased has withdrawn the basis the
        // account operates on, so authenticating them while the deadline runs is processing they
        // have objected to
        Assert.True(person.IsDeleted);
        Assert.Equal((int)DeletionKinds.SubjectRequested, person.DeletionKind);
        Assert.NotNull(person.ErasureRequestedAt);
        Assert.NotNull(person.ErasureDueAt);
        Assert.Null(person.ErasureBlockedReason);
    }

    [UnitFact]
    public async Task GivenTheWrongPassword_WhenRequestingErasure_ThenNothingIsRecorded()
    {
        // A bearer token is not proof the person is present. One left open on a shared machine must
        // not be enough to destroy the account it belongs to.
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var person = await SeedPersonAsync(persons);

        var output = await Handler(persons, googleUsers)
            .HandleAsync(Command(person.PublicId, password: WrongPassword));

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.CredentialNotAccepted, output.Errors);
        Assert.False(person.IsDeleted);
        Assert.Null(person.ErasureRequestedAt);
    }

    [UnitFact]
    public async Task GivenNoCredential_WhenRequestingErasure_ThenItIsRefused()
    {
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var person = await SeedPersonAsync(persons);

        var output = await Handler(persons, googleUsers)
            .HandleAsync(Command(person.PublicId, password: null));

        Assert.False(output.Success);
        Assert.False(person.IsDeleted);
    }

    [UnitFact]
    public async Task GivenAnErasureAlreadyRequested_WhenRequestingAgain_ThenItIsRefused()
    {
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var person = await SeedPersonAsync(persons, erasureRequestedAt: DateTime.UtcNow.AddDays(-1));

        // Deliberately with the wrong password: the repeat is caught before the credential, so a
        // repeat costs no Argon2id derivation and the answer does not depend on getting it right twice
        var output = await Handler(persons, googleUsers)
            .HandleAsync(Command(person.PublicId, password: WrongPassword));

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.ErasureAlreadyRequested, output.Errors);
    }

    [UnitFact]
    public async Task GivenATokenNamingNobody_WhenRequestingErasure_ThenItIsRefused()
    {
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();

        var output = await Handler(persons, googleUsers).HandleAsync(Command(Guid.NewGuid()));

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.NotEligible, output.Errors);
    }

    [UnitFact]
    public async Task GivenTheLastOwnerOfAScope_WhenRequestingErasure_ThenItIsRecordedButBlocked()
    {
        // NFR-12: suspending them would leave the scope ownerless. The request is still recorded and
        // the deadline still runs — the subject's right does not depend on the scope's ownership
        // arrangements, and Art. 12(3)'s clock starts at the request.
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var person = await SeedPersonAsync(persons, Roles.ScopeAdmin, ownedScopeIds: 7);

        var output = await Handler(persons, googleUsers).HandleAsync(Command(person.PublicId));

        Assert.True(output.Success);
        Assert.Equal(ErasureMessages.ErasureRequestedButBlocked, output.Messages.First());
        Assert.True(output.Data!.Blocked);
        Assert.Equal(ErasureMessages.BlockedByLastScopeOwnership, output.Data.BlockedReason);

        // Recorded and running, but not suspended — the scope keeps its owner
        Assert.NotNull(person.ErasureRequestedAt);
        Assert.NotNull(person.ErasureDueAt);
        Assert.False(person.IsDeleted);
        Assert.Equal(ErasureMessages.BlockedByLastScopeOwnership, person.ErasureBlockedReason);
    }

    [UnitFact]
    public async Task GivenAScopeWithAnotherOwner_WhenRequestingErasure_ThenItProceeds()
    {
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var person = await SeedPersonAsync(persons, Roles.ScopeAdmin, ownedScopeIds: 7);
        await SeedPersonAsync(persons, Roles.ScopeAdmin, ownedScopeIds: 7);

        var output = await Handler(persons, googleUsers).HandleAsync(Command(person.PublicId));

        Assert.True(output.Success);
        Assert.False(output.Data!.Blocked);
        Assert.True(person.IsDeleted);
    }

    [UnitFact]
    public async Task GivenAGoogleUserWithAFreshToken_WhenRequestingErasure_ThenItIsRecorded()
    {
        // A Google User has no password, so the equivalent proof of presence is a token Google
        // minted for them just now
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);

        var output = await Handler(persons, googleUsers, Payload())
            .HandleAsync(Command(googleUser.PublicId, password: null, idToken: "a-fresh-token"));

        Assert.True(output.Success);
        Assert.False(output.Data!.Blocked);
        Assert.True(googleUser.IsDeleted);
        Assert.Equal((int)DeletionKinds.SubjectRequested, googleUser.DeletionKind);
    }

    [UnitFact]
    public async Task GivenATokenForADifferentGoogleAccount_WhenRequestingErasure_ThenItIsRefused()
    {
        // Verifying the signature is not enough: without matching the subject claim, any valid token
        // for any Google account would erase whichever account the bearer token named.
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);

        var output = await Handler(persons, googleUsers, Payload(subject: "999999999999999999999"))
            .HandleAsync(Command(googleUser.PublicId, password: null, idToken: "someone-elses-token"));

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.CredentialNotAccepted, output.Errors);
        Assert.False(googleUser.IsDeleted);
    }

    [UnitFact]
    public async Task GivenAnUnverifiableToken_WhenRequestingErasure_ThenItIsRefused()
    {
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);

        var output = await Handler(persons, googleUsers, payload: null)
            .HandleAsync(Command(googleUser.PublicId, password: null, idToken: "forged"));

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.CredentialNotAccepted, output.Errors);
        Assert.False(googleUser.IsDeleted);
    }

    [UnitFact]
    public async Task GivenAGoogleUserPresentingAPassword_WhenRequestingErasure_ThenItIsRefused()
    {
        // The credential required is decided by identity type, not by what the caller chose to send
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);

        var output = await Handler(persons, googleUsers, Payload())
            .HandleAsync(Command(googleUser.PublicId, password: Password, idToken: null));

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.CredentialNotAccepted, output.Errors);
        Assert.False(googleUser.IsDeleted);
    }
}
