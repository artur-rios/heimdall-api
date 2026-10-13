using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for PendingRegistrationConfirmer (UC-47 confirmation, reached through UC-14). Each test
// asserts the outcome and what exists afterwards: the outcome says why, the rows say whether anything
// was created that should not have been.
public class PendingRegistrationConfirmerTests
{
    private const string Email = "new@test.local";
    private const string Password = "Str0ng-Confirm-Pass!";

    // A person store whose create fails as the database does when the per-scope unique index refuses the
    // insert; everything else is the fake. The explicit implementation is what the confirmer's
    // IAsyncRepository dependency reaches.
    private sealed class UniqueViolationPersonRepository
        : AsyncFakeRepository<Person, long>, IAsyncRepository<Person, long>
    {
        public List<Person> Attempted { get; } = [];

        Task<DataOutput<long>> IAsyncRepository<Person, long>.CreateAsync(
            Person entity, CancellationToken cancellationToken)
        {
            Attempted.Add(entity);
            return Task.FromResult(DataOutput<long>.New.WithError(RelationalErrors.UniqueViolationMessage));
        }
    }

    // A pending-registration store whose delete fails as EF does when the row it re-read was deleted by a
    // concurrent request first: the repository reports that as the concurrency message.
    private sealed class ConcurrentlyClaimedPendingRepository
        : AsyncFakeRepository<PendingRegistration, long>, IAsyncRepository<PendingRegistration, long>
    {
        Task<DataOutput<IEnumerable<long>>> IAsyncRepository<PendingRegistration, long>.DeleteRangeAsync(
            IEnumerable<long> ids, CancellationToken cancellationToken) =>
            Task.FromResult(DataOutput<IEnumerable<long>>.New.WithError(RelationalErrors.ConcurrencyMessage));
    }

    private sealed class Fixture
    {
        public AsyncFakeRepository<Scope, long> Scopes { get; } = new();
        public AsyncFakeRepository<Person, long> Persons { get; } = new();
        public AsyncFakeRepository<GoogleUser, long> GoogleUsers { get; } = new();
        public AsyncFakeRepository<PendingRegistration, long> Pending { get; } = new();

        public PendingRegistrationConfirmer Confirmer(
            IAsyncRepository<Person, long>? personWriter = null,
            IAsyncReadOnlyRepository<PendingRegistration, long>? pendingReader = null,
            IAsyncRepository<PendingRegistration, long>? pendingWriter = null,
            ILogger<PendingRegistrationConfirmer>? logger = null) =>
            new(pendingReader ?? Pending, pendingWriter ?? Pending, Persons, GoogleUsers,
                personWriter ?? Persons, logger ?? NullLogger<PendingRegistrationConfirmer>.Instance);

        public async Task<Scope> ScopeAsync(bool open = true, bool isDeleted = false)
        {
            var scope = new Scope
            {
                PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}",
                SelfRegistrationEnabled = open, IsDeleted = isDeleted
            };
            await Scopes.CreateAsync(scope);
            return scope;
        }

        public async Task<string> PendingAsync(
            Scope scope, TimeSpan? lifetime = null, string email = Email,
            AsyncFakeRepository<PendingRegistration, long>? into = null)
        {
            var token = SingleUseToken.New();
            var (hash, salt) = await PasswordHashGate.Shared.EncodeWithRandomSaltAsync(Password);
            await (into ?? Pending).CreateAsync(new PendingRegistration
            {
                ScopeId = scope.Id, Scope = scope, Name = "New Person", Email = email,
                PasswordHash = hash, Salt = salt, TokenHash = SingleUseTokenHash.Of(token),
                ExpiresAt = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(1)),
                CreatedAt = DateTime.UtcNow
            });
            return token;
        }

        public List<Person> People() => Persons.Query().ToList();
        public List<PendingRegistration> Waiting() => Pending.Query().ToList();
    }

    private static Task<PendingRegistrationConfirmation> ConfirmAsync(
        PendingRegistrationConfirmer confirmer, string token, string? password = Password) =>
        confirmer.ConfirmAsync(SingleUseTokenHash.Of(token), password, DateTime.UtcNow);

    [UnitFact]
    public async Task GivenAValidPendingToken_WhenConfirming_ThenAVerifiedUserIsCreatedAndThePendingRowRemoved()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        var token = await fixture.PendingAsync(scope);

        var result = await ConfirmAsync(fixture.Confirmer(), token);

        Assert.Equal(PendingRegistrationOutcome.Confirmed, result.Outcome);
        var person = Assert.Single(fixture.People());
        Assert.Equal((long)Roles.User, person.RoleId);
        Assert.True(person.EmailVerified);
        Assert.Equal("New Person", person.Name);
        Assert.Equal(Email, person.Email);
        Assert.True(await PasswordHashGate.Shared.TextMatchesAsync(Password, person.PasswordHash, person.Salt));
        Assert.Equal(scope.Id, person.ScopeId);
        Assert.Equal(scope.Id, person.ScopeMembership!.ScopeId);
        Assert.NotNull(person.BasisRecordedAt);
        Assert.Empty(fixture.Waiting());
    }

    [UnitFact]
    public async Task GivenTwoPendingRowsForTheAddress_WhenOneIsConfirmed_ThenTheOtherIsRemovedToo()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        var first = await fixture.PendingAsync(scope);
        await fixture.PendingAsync(scope, email: Email.ToUpper());
        var unrelated = await fixture.PendingAsync(scope, email: "someone@test.local");

        var result = await ConfirmAsync(fixture.Confirmer(), first);

        Assert.Equal(PendingRegistrationOutcome.Confirmed, result.Outcome);
        Assert.Single(fixture.People());
        var remaining = Assert.Single(fixture.Waiting());
        Assert.Equal(SingleUseTokenHash.Of(unrelated), remaining.TokenHash);
    }

    [UnitFact]
    public async Task GivenAnUnknownToken_WhenConfirming_ThenNotFound()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        await fixture.PendingAsync(scope);

        var result = await ConfirmAsync(fixture.Confirmer(), SingleUseToken.New());

        Assert.Equal(PendingRegistrationOutcome.NotFound, result.Outcome);
        Assert.Empty(fixture.People());
        Assert.Single(fixture.Waiting());
    }

    [UnitFact]
    public async Task GivenAnExpiredPendingToken_WhenConfirming_ThenExpiredAndNoPerson()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        var token = await fixture.PendingAsync(scope, TimeSpan.FromMinutes(-1));

        var result = await ConfirmAsync(fixture.Confirmer(), token);

        Assert.Equal(PendingRegistrationOutcome.Expired, result.Outcome);
        Assert.Empty(fixture.People());
        Assert.Single(fixture.Waiting());
    }

    [UnitFact]
    public async Task GivenNoPassword_WhenConfirming_ThenPasswordRequiredAndTheRowIsKept()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        var token = await fixture.PendingAsync(scope);

        var missing = await ConfirmAsync(fixture.Confirmer(), token, null);
        var empty = await ConfirmAsync(fixture.Confirmer(), token, string.Empty);

        Assert.Equal(PendingRegistrationOutcome.PasswordRequired, missing.Outcome);
        Assert.Equal(PendingRegistrationOutcome.PasswordRequired, empty.Outcome);
        Assert.Empty(fixture.People());
        Assert.Single(fixture.Waiting());
    }

    [UnitFact]
    public async Task GivenAWrongPassword_WhenConfirming_ThenPasswordIncorrectAndTheRowIsKept()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        var token = await fixture.PendingAsync(scope);

        var wrong = await ConfirmAsync(fixture.Confirmer(), token, "Wr0ng-Confirm-Pass!");

        Assert.Equal(PendingRegistrationOutcome.PasswordIncorrect, wrong.Outcome);
        Assert.Empty(fixture.People());
        Assert.Single(fixture.Waiting());

        // The refusal consumed nothing, so the registrant can still finish with the right password.
        var right = await ConfirmAsync(fixture.Confirmer(), token);

        Assert.Equal(PendingRegistrationOutcome.Confirmed, right.Outcome);
        Assert.Single(fixture.People());
        Assert.Empty(fixture.Waiting());
    }

    [UnitFact]
    public async Task GivenAnExpiredTokenAndNoPassword_WhenConfirming_ThenExpired()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        var token = await fixture.PendingAsync(scope, TimeSpan.FromMinutes(-1));

        var result = await ConfirmAsync(fixture.Confirmer(), token, null);

        Assert.Equal(PendingRegistrationOutcome.Expired, result.Outcome);
    }

    [UnitFact]
    public async Task GivenTheScopeClosedSinceRegistering_WhenConfirming_ThenRefusedAndNoPerson()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        var token = await fixture.PendingAsync(scope);
        scope.SelfRegistrationEnabled = false;

        var result = await ConfirmAsync(fixture.Confirmer(), token);

        Assert.Equal(PendingRegistrationOutcome.Refused, result.Outcome);
        Assert.Empty(fixture.People());
        Assert.Empty(fixture.Waiting());
    }

    [UnitFact]
    public async Task GivenTheScopeDeletedSinceRegistering_WhenConfirming_ThenRefusedAndNoPerson()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        var token = await fixture.PendingAsync(scope);
        scope.IsDeleted = true;

        var result = await ConfirmAsync(fixture.Confirmer(), token);

        Assert.Equal(PendingRegistrationOutcome.Refused, result.Outcome);
        Assert.Empty(fixture.People());
        Assert.Empty(fixture.Waiting());
    }

    [UnitFact]
    public async Task GivenTheAddressTakenByAPersonSinceRegistering_WhenConfirming_ThenRefusedAndNoSecondPerson()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        var token = await fixture.PendingAsync(scope);
        await fixture.Persons.CreateAsync(new Person
        {
            PublicId = Guid.NewGuid(), Name = "Holder", Email = Email.ToUpper(), RoleId = (long)Roles.User,
            ScopeId = scope.Id, ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope }
        });

        var result = await ConfirmAsync(fixture.Confirmer(), token);

        Assert.Equal(PendingRegistrationOutcome.Refused, result.Outcome);
        Assert.Equal("Holder", Assert.Single(fixture.People()).Name);
        Assert.Empty(fixture.Waiting());
    }

    [UnitFact]
    public async Task GivenTheAddressTakenByAGoogleUserSinceRegistering_WhenConfirming_ThenRefusedAndNoPerson()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        var token = await fixture.PendingAsync(scope);
        await fixture.GoogleUsers.CreateAsync(new GoogleUser
        {
            PublicId = Guid.NewGuid(), Name = "Google", Email = Email, GoogleId = "sub", ScopeId = scope.Id
        });

        var result = await ConfirmAsync(fixture.Confirmer(), token);

        Assert.Equal(PendingRegistrationOutcome.Refused, result.Outcome);
        Assert.Empty(fixture.People());
        Assert.Empty(fixture.Waiting());
    }

    [UnitFact]
    public async Task GivenAUniqueViolationOnCreate_WhenConfirming_ThenRefused()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        var token = await fixture.PendingAsync(scope);

        var persons = new UniqueViolationPersonRepository();

        var result = await ConfirmAsync(fixture.Confirmer(persons), token);

        // The create was attempted (the index, not an earlier check, refused it), nothing was kept, and
        // the claimed row is consumed.
        Assert.Equal(PendingRegistrationOutcome.Refused, result.Outcome);
        Assert.Single(persons.Attempted);
        Assert.Empty(persons.Query().ToList());
        Assert.Empty(fixture.Waiting());
    }

    [UnitFact]
    public async Task GivenTheSameTokenTwice_WhenConfirming_ThenTheSecondIsAlreadyClaimed()
    {
        // A second request that read the row before the first deleted it: its reader still holds the
        // row, the table it deletes from no longer does.
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        var stale = new AsyncFakeRepository<PendingRegistration, long>();
        var token = await fixture.PendingAsync(scope);
        await fixture.PendingAsync(scope, into: stale);
        var staleToken = Assert.Single(stale.Query().ToList()).TokenHash;

        var first = await ConfirmAsync(fixture.Confirmer(), token);
        var second = await fixture.Confirmer(pendingReader: stale).ConfirmAsync(staleToken, Password, DateTime.UtcNow);

        Assert.Equal(PendingRegistrationOutcome.Confirmed, first.Outcome);
        Assert.Equal(PendingRegistrationOutcome.AlreadyClaimed, second.Outcome);
        Assert.Single(fixture.People());
    }

    [UnitFact]
    public async Task GivenAConcurrentClaimOfTheSameToken_WhenConfirming_ThenAlreadyClaimedAndNothingLoggedAsAnError()
    {
        // Two requests read the row before either deleted it: the loser's delete reports the concurrency
        // message. That is a lost claim, not a failure, so a double submit does not see a conflict.
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        var pending = new ConcurrentlyClaimedPendingRepository();
        var token = await fixture.PendingAsync(scope, into: pending);
        var logger = new RecordingLogger<PendingRegistrationConfirmer>();

        var result = await ConfirmAsync(
            fixture.Confirmer(pendingReader: pending, pendingWriter: pending, logger: logger), token);

        Assert.Equal(PendingRegistrationOutcome.AlreadyClaimed, result.Outcome);
        Assert.Empty(result.Errors);
        Assert.Empty(fixture.People());
        Assert.DoesNotContain(LogLevel.Error, logger.Entries.Select(entry => entry.Level));
    }
}
