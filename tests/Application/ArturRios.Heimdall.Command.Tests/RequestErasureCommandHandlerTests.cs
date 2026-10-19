using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for RequestErasureCommandHandler (UC-42). Four properties matter more than the rest:
// that the subject is always the caller, that a bearer token alone cannot trigger an irreversible
// operation, that a request NFR-12 blocks is still recorded with its deadline running rather than
// refused, and that a request lifts the subject's own restriction rather than waiting behind it
// (NFR-24).
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
        AsyncFakeRepository<Person, long> persons,
        AsyncFakeRepository<GoogleUser, long> googleUsers,
        GoogleIdTokenPayload? payload = null,
        IAtomicWrites? atomicWrites = null) =>
        new(new RequestErasureCommandValidator(),
            persons, googleUsers, googleUsers,
            Verifier(payload), Retention(), atomicWrites ?? InMemoryAtomicWrites.Instance);

    private static async Task<Person> SeedPersonAsync(
        AsyncFakeRepository<Person, long> persons,
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

    private static async Task<GoogleUser> SeedGoogleUserAsync(AsyncFakeRepository<GoogleUser, long> googleUsers)
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
    public async Task GivenARequestRecordedSinceTheRead_WhenRequestingErasure_ThenTheFirstDeadlineStands()
    {
        // Given a request another call records between this one's checks and its write
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var person = await SeedPersonAsync(persons);
        var firstRequest = DateTime.UtcNow.AddMinutes(-1);
        var atomicWrites = new InMemoryAtomicWrites
        {
            BeforeConditionalWrite = row =>
            {
                row.ErasureRequestedAt = firstRequest;
                row.ErasureDueAt = firstRequest + ErasureDeadline;
            }
        };

        // When
        var output = await Handler(persons, googleUsers, atomicWrites: atomicWrites)
            .HandleAsync(Command(person.PublicId));

        // Then it is refused as AF-42c, and the deadline that attached first is not moved
        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.ErasureAlreadyRequested, output.Errors);
        Assert.Equal(firstRequest, person.ErasureRequestedAt);
        Assert.Equal(firstRequest + ErasureDeadline, person.ErasureDueAt);
    }

    [UnitFact]
    public async Task GivenAPersonWithTheirPassword_WhenRequestingErasure_ThenItIsRecordedAndTheyAreSuspended()
    {
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
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
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var person = await SeedPersonAsync(persons);

        var output = await Handler(persons, googleUsers)
            .HandleAsync(Command(person.PublicId, password: WrongPassword));

        Assert.False(output.Success);
        Assert.Contains(AuthMessages.PasswordOrCodeIncorrect, output.Errors);
        Assert.False(person.IsDeleted);
        Assert.Null(person.ErasureRequestedAt);
    }

    [UnitFact]
    public async Task GivenNoCredential_WhenRequestingErasure_ThenItIsRefused()
    {
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var person = await SeedPersonAsync(persons);

        var output = await Handler(persons, googleUsers)
            .HandleAsync(Command(person.PublicId, password: null));

        Assert.False(output.Success);
        Assert.False(person.IsDeleted);
    }

    [UnitFact]
    public async Task GivenAnErasureAlreadyRequested_WhenRequestingAgain_ThenItIsRefused()
    {
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var person = await SeedPersonAsync(persons, erasureRequestedAt: DateTime.UtcNow.AddDays(-1));

        // Deliberately with the wrong password: the repeat is caught before the credential, so a
        // repeat costs no Argon2id derivation and the answer does not depend on getting it right twice
        var output = await Handler(persons, googleUsers)
            .HandleAsync(Command(person.PublicId, password: WrongPassword));

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.ErasureAlreadyRequested, output.Errors);
    }

    [UnitFact]
    public async Task GivenASuspendedPersonWhoAlreadyAsked_WhenRequestingAgain_ThenTheRepeatIsNamed()
    {
        // A subject suspended by their own request reaches this handler (the endpoint is a data
        // subject right, so the liveness filter lets them through). AF-42c — "already requested" —
        // is the true answer for them, not AF-42b's generic "not eligible".
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var person = await SeedPersonAsync(persons, erasureRequestedAt: DateTime.UtcNow.AddDays(-1));
        person.IsDeleted = true;

        var output = await Handler(persons, googleUsers).HandleAsync(Command(person.PublicId));

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.ErasureAlreadyRequested, output.Errors);
    }

    [UnitFact]
    public async Task GivenAPersonSuspendedByAnAdministrator_WhenRequestingErasure_ThenNotEligible()
    {
        // AF-42b: a logically deleted identity that never asked is not a live one
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var person = await SeedPersonAsync(persons);
        person.IsDeleted = true;

        var output = await Handler(persons, googleUsers).HandleAsync(Command(person.PublicId));

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.NotEligible, output.Errors);
        Assert.Null(person.ErasureRequestedAt);
    }

    [UnitFact]
    public async Task GivenATokenNamingNobody_WhenRequestingErasure_ThenItIsRefused()
    {
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();

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
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
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
    public async Task GivenAnUnrestrictedPerson_WhenRequestingErasure_ThenNoRestrictionIsReportedLifted()
    {
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var person = await SeedPersonAsync(persons);

        var output = await Handler(persons, googleUsers).HandleAsync(Command(person.PublicId));

        Assert.True(output.Success);
        Assert.False(output.Data!.RestrictionLifted);

        // Nothing was lifted, so the audit trail records the request alone
        Assert.Empty(((IAuditedSideEffects)output.Data).AuditedSideEffects());
    }

    [UnitFact]
    public async Task GivenARestrictedPerson_WhenRequestingErasure_ThenTheRestrictionIsLiftedWithTheRequest()
    {
        // NFR-24 keeps a restricted record out of the anonymisation pass, so a request left behind the
        // restriction would wait unerased while its deadline ran out. Only the subject can impose a
        // restriction (UC-44), and asking to be erased — credential in hand — is their later and
        // more specific instruction.
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var person = await SeedPersonAsync(persons);
        person.ProcessingRestrictedAt = DateTime.UtcNow.AddDays(-3);
        person.RestrictionGround = (int)RestrictionGrounds.AccuracyContested;

        var output = await Handler(persons, googleUsers).HandleAsync(Command(person.PublicId));

        Assert.True(output.Success);
        Assert.Equal(ErasureMessages.ErasureRequested, output.Messages.First());
        Assert.True(output.Data!.RestrictionLifted);

        // Lifted as UC-45 lifts a subject's own: no Art. 18(3) notice is owed to the person lifting it
        Assert.Null(person.ProcessingRestrictedAt);
        Assert.Null(person.RestrictionGround);
        Assert.Null(person.RestrictionLiftNotifiedAt);

        // And the request proceeds exactly as it would have unrestricted
        Assert.True(person.IsDeleted);
        Assert.Equal((int)DeletionKinds.SubjectRequested, person.DeletionKind);
        Assert.NotNull(person.ErasureDueAt);

        // The lift is recorded in the audit trail under the name a reader looking for lifts searches
        Assert.Equal(
            [nameof(LiftProcessingRestrictionCommand)],
            ((IAuditedSideEffects)output.Data).AuditedSideEffects());
    }

    [UnitFact]
    public async Task GivenARestrictedLastOwnerOfAScope_WhenRequestingErasure_ThenTheRestrictionIsLiftedThoughBlocked()
    {
        // AF-42d: the suspension waits for an owner to be transferred, but the restriction does not
        // wait with it — the request's deadline is running either way, and a subject left restricted
        // could take no part in the transfer that unblocks it.
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var person = await SeedPersonAsync(persons, Roles.ScopeAdmin, ownedScopeIds: 7);
        person.ProcessingRestrictedAt = DateTime.UtcNow.AddDays(-3);
        person.RestrictionGround = (int)RestrictionGrounds.ObjectionPending;

        var output = await Handler(persons, googleUsers).HandleAsync(Command(person.PublicId));

        Assert.True(output.Success);
        Assert.Equal(ErasureMessages.ErasureRequestedButBlocked, output.Messages.First());
        Assert.True(output.Data!.Blocked);
        Assert.True(output.Data.RestrictionLifted);
        Assert.Null(person.ProcessingRestrictedAt);
        Assert.Null(person.RestrictionGround);
        Assert.False(person.IsDeleted);
        Assert.NotNull(person.ErasureRequestedAt);
    }

    [UnitFact]
    public async Task GivenARestrictedPersonWithTheWrongPassword_WhenRequestingErasure_ThenTheRestrictionStands()
    {
        // The lift is part of the request, so a refused request lifts nothing
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var person = await SeedPersonAsync(persons);
        var restrictedAt = DateTime.UtcNow.AddDays(-3);
        person.ProcessingRestrictedAt = restrictedAt;
        person.RestrictionGround = (int)RestrictionGrounds.AccuracyContested;

        var output = await Handler(persons, googleUsers)
            .HandleAsync(Command(person.PublicId, password: WrongPassword));

        Assert.False(output.Success);
        Assert.Equal(restrictedAt, person.ProcessingRestrictedAt);
        Assert.Equal((int)RestrictionGrounds.AccuracyContested, person.RestrictionGround);
    }

    [UnitFact]
    public async Task GivenARestrictionImposedAfterTheRequest_WhenRequestingAgain_ThenTheRestrictionStands()
    {
        // A subject pending erasure may restrict afterwards (UC-44) — that later instruction is
        // theirs to lift (UC-45). Repeating the erasure request is AF-42c and changes nothing.
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var person = await SeedPersonAsync(persons, erasureRequestedAt: DateTime.UtcNow.AddDays(-5));
        person.IsDeleted = true;
        person.ProcessingRestrictedAt = DateTime.UtcNow.AddDays(-1);

        var output = await Handler(persons, googleUsers).HandleAsync(Command(person.PublicId));

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.ErasureAlreadyRequested, output.Errors);
        Assert.NotNull(person.ProcessingRestrictedAt);
    }

    [UnitFact]
    public async Task GivenAScopeWithAnotherOwner_WhenRequestingErasure_ThenItProceeds()
    {
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
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
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);

        var output = await Handler(persons, googleUsers, Payload())
            .HandleAsync(Command(googleUser.PublicId, password: null, idToken: "a-fresh-token"));

        Assert.True(output.Success);
        Assert.False(output.Data!.Blocked);
        Assert.True(googleUser.IsDeleted);
        Assert.Equal((int)DeletionKinds.SubjectRequested, googleUser.DeletionKind);
        Assert.False(output.Data.RestrictionLifted);
    }

    [UnitFact]
    public async Task GivenAGoogleTokenAlreadyUsed_WhenRequestingErasure_ThenTheCredentialIsNotAcceptedAndNothingIsWritten()
    {
        // Given (FR-GO-21) a token already used once, to sign in say
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);
        var atomicWrites = new InMemoryAtomicWrites
        {
            GoogleIdTokenUses = new HashSet<string> { GoogleIdTokenUse.KeyOf(null, "a-used-token") }
        };

        // When
        var output = await Handler(persons, googleUsers, Payload(), atomicWrites)
            .HandleAsync(Command(googleUser.PublicId, password: null, idToken: "a-used-token"));

        // Then it is refused as an unverifiable token is
        Assert.False(output.Success);
        Assert.Equal([ErasureMessages.CredentialNotAccepted], output.Errors);
        Assert.Null(googleUser.ErasureRequestedAt);
        Assert.False(googleUser.IsDeleted);
    }

    [UnitFact]
    public async Task GivenARestrictedGoogleUser_WhenRequestingErasure_ThenTheRestrictionIsLiftedWithTheRequest()
    {
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);
        googleUser.ProcessingRestrictedAt = DateTime.UtcNow.AddDays(-3);
        googleUser.RestrictionGround = (int)RestrictionGrounds.UnlawfulButErasureOpposed;

        var output = await Handler(persons, googleUsers, Payload())
            .HandleAsync(Command(googleUser.PublicId, password: null, idToken: "a-fresh-token"));

        Assert.True(output.Success);
        Assert.True(output.Data!.RestrictionLifted);
        Assert.Null(googleUser.ProcessingRestrictedAt);
        Assert.Null(googleUser.RestrictionGround);
        Assert.Null(googleUser.RestrictionLiftNotifiedAt);
        Assert.True(googleUser.IsDeleted);
        Assert.Equal(
            [nameof(LiftProcessingRestrictionCommand)],
            ((IAuditedSideEffects)output.Data).AuditedSideEffects());
    }

    [UnitFact]
    public async Task GivenARestrictedGoogleUserWithAnUnverifiableToken_WhenRequestingErasure_ThenTheRestrictionStands()
    {
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);
        googleUser.ProcessingRestrictedAt = DateTime.UtcNow.AddDays(-3);
        googleUser.RestrictionGround = (int)RestrictionGrounds.UnlawfulButErasureOpposed;

        var output = await Handler(persons, googleUsers, payload: null)
            .HandleAsync(Command(googleUser.PublicId, password: null, idToken: "forged"));

        Assert.False(output.Success);
        Assert.NotNull(googleUser.ProcessingRestrictedAt);
        Assert.False(googleUser.IsDeleted);
    }

    [UnitFact]
    public async Task GivenATokenForADifferentGoogleAccount_WhenRequestingErasure_ThenItIsRefused()
    {
        // Verifying the signature is not enough: without matching the subject claim, any valid token
        // for any Google account would erase whichever account the bearer token named.
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
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
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
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
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);

        var output = await Handler(persons, googleUsers, Payload())
            .HandleAsync(Command(googleUser.PublicId, password: Password, idToken: null));

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.CredentialNotAccepted, output.Errors);
        Assert.False(googleUser.IsDeleted);
    }

    // FR-AU-13: the re-authentication budget

    [UnitFact]
    public async Task GivenAPausedBudget_WhenAPersonPresentsTheRightPassword_ThenNothingIsRecorded()
    {
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var person = await SeedPersonAsync(persons);
        person.ReauthenticationPausedUntil = DateTime.UtcNow.AddMinutes(10);

        var output = await Handler(persons, googleUsers).HandleAsync(Command(person.PublicId));

        Assert.Equal([AuthMessages.PasswordOrCodeIncorrect], output.Errors);
        Assert.False(person.IsDeleted);
        Assert.Null(person.ErasureRequestedAt);
    }

    [UnitFact]
    public async Task GivenFiveWrongPasswords_WhenCounted_ThenReauthenticationPauses()
    {
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var person = await SeedPersonAsync(persons);

        for (var attempt = 0; attempt < ReauthenticationBudget.MaxFailures; attempt++)
        {
            await Handler(persons, googleUsers).HandleAsync(Command(person.PublicId, password: WrongPassword));
        }

        Assert.NotNull(person.ReauthenticationPausedUntil);
        Assert.Equal(0, person.FailedLoginAttempts);
    }

    [UnitFact]
    public async Task GivenAPersonPresentingNoCredential_WhenRequestingErasure_ThenTheSharedRefusalIsChargedToTheBudget()
    {
        // A missing password is a wrong one: refused as every re-authentication is, and counted
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var person = await SeedPersonAsync(persons);

        var output = await Handler(persons, googleUsers).HandleAsync(Command(person.PublicId, password: null));

        Assert.Equal([AuthMessages.PasswordOrCodeIncorrect], output.Errors);
        Assert.Equal(1, person.FailedReauthenticationAttempts);
        Assert.False(person.IsDeleted);
    }

    [UnitFact]
    public async Task GivenAGoogleUserPresentingNoCredential_WhenRequestingErasure_ThenTheCredentialIsNotAccepted()
    {
        var persons = new AsyncFakeRepository<Person, long>();
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);

        var output = await Handler(persons, googleUsers, Payload())
            .HandleAsync(Command(googleUser.PublicId, password: null, idToken: null));

        Assert.Equal([ErasureMessages.CredentialNotAccepted], output.Errors);
        Assert.False(googleUser.IsDeleted);
    }
}
