using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for RegistrationProcessor (UC-47 steps 4 to 6, run after the caller has been answered).
// What separates the flows is what was created and what was mailed, so each test asserts both. The
// mail each address can be sent through registering is bounded twice: at most three live pending
// registrations per scope and address (FR-SR-13), and at most three attempt notices per address a
// day (FR-SR-14).
public class RegistrationProcessorTests
{
    private sealed class RecordingLinkSender : IRegistrationLinkSender
    {
        public List<(string Email, string Token)> Sent { get; } = [];

        public List<string?> LinkBases { get; } = [];

        public Task SendAsync(string email, string token, string? linkBase)
        {
            Sent.Add((email, token));
            LinkBases.Add(linkBase);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAttemptSender : IRegistrationAttemptSender
    {
        public List<(string Email, bool GoogleAccount)> Sent { get; } = [];

        public Task SendAsync(string email, bool googleAccount)
        {
            Sent.Add((email, googleAccount));
            return Task.CompletedTask;
        }
    }

    // A pending store whose create fails, as it does when the database refuses the insert.
    private sealed class FailingPendingRepository
        : AsyncFakeRepository<PendingRegistration, long>, IAsyncRepository<PendingRegistration, long>
    {
        public int Attempts { get; private set; }

        Task<DataOutput<long>> IAsyncRepository<PendingRegistration, long>.CreateAsync(
            PendingRegistration entity, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(DataOutput<long>.New.WithError("The insert was refused."));
        }
    }

    private sealed class Fixture
    {
        public AsyncFakeRepository<Scope, long> Scopes { get; } = new();
        public AsyncFakeRepository<Person, long> Persons { get; } = new();
        public AsyncFakeRepository<GoogleUser, long> GoogleUsers { get; } = new();
        public AsyncFakeRepository<PendingRegistration, long> Pending { get; } = new();
        public RecordingLinkSender Links { get; } = new();
        public RecordingAttemptSender Attempts { get; } = new();
        public InMemoryAttemptNoticeBudgets NoticeBudgets { get; } = new();
        public EmailVerificationOptions Options { get; } = new() { TokenLifetime = TimeSpan.FromHours(24) };

        public RegistrationProcessor Processor(
            IAsyncRepository<PendingRegistration, long>? pendingWriter = null,
            ILogger<RegistrationProcessor>? logger = null) =>
            new(Scopes, Persons, GoogleUsers, Pending, pendingWriter ?? Pending, Links, Attempts,
                NoticeBudgets, Options, logger ?? NullLogger<RegistrationProcessor>.Instance);

        public async Task<PendingRegistration> PendingAsync(Scope scope, string email, DateTime expiresAt)
        {
            var pending = new PendingRegistration
            {
                ScopeId = scope.Id, Name = "Earlier", Email = email, PasswordHash = [1], Salt = [2],
                TokenHash = Guid.NewGuid().ToString("N"), ExpiresAt = expiresAt, CreatedAt = DateTime.UtcNow
            };
            await Pending.CreateAsync(pending);
            return pending;
        }

        public async Task<Scope> ScopeAsync(
            bool open = true, bool isDeleted = false, string? emailVerificationUrl = null)
        {
            var scope = new Scope
            {
                PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}",
                SelfRegistrationEnabled = open, IsDeleted = isDeleted,
                EmailVerificationUrl = emailVerificationUrl
            };
            await Scopes.CreateAsync(scope);
            return scope;
        }

        public async Task<Person> UserAsync(Scope scope, string email, DateTime? restrictedAt = null)
        {
            var person = new Person
            {
                PublicId = Guid.NewGuid(), Name = "Existing", Email = email, RoleId = (long)Roles.User,
                ScopeId = scope.Id, ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope },
                ProcessingRestrictedAt = restrictedAt
            };
            await Persons.CreateAsync(person);
            return person;
        }
    }

    private static RegistrationRequest Request(Scope scope, string email = "new@test.local") =>
        new(scope.PublicId, "New Person", email, [1, 2, 3], [4, 5, 6]);

    [UnitFact]
    public async Task GivenAFreeAddress_WhenProcessing_ThenAPendingRegistrationIsWrittenAndItsLinkMailed()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        var before = DateTime.UtcNow;

        await fixture.Processor().ProcessAsync(Request(scope));

        Assert.Empty(fixture.Persons.Query().ToList());
        var pending = Assert.Single(fixture.Pending.Query().ToList());
        Assert.Equal(scope.Id, pending.ScopeId);
        Assert.Equal("New Person", pending.Name);
        Assert.Equal("new@test.local", pending.Email);
        Assert.Equal(new byte[] { 1, 2, 3 }, pending.PasswordHash);
        Assert.Equal(new byte[] { 4, 5, 6 }, pending.Salt);
        Assert.InRange(pending.ExpiresAt, before.AddHours(24), DateTime.UtcNow.AddHours(24));
        var (email, token) = Assert.Single(fixture.Links.Sent);
        Assert.Equal("new@test.local", email);
        Assert.Equal(48, token.Length);
        Assert.Equal(SingleUseTokenHash.Of(token), pending.TokenHash);
        Assert.Empty(fixture.Attempts.Sent);
    }

    [UnitFact]
    public async Task GivenAnAddressWithOnlyAPendingRegistration_WhenProcessing_ThenASecondPendingRowIsWritten()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();

        await fixture.Processor().ProcessAsync(Request(scope));
        await fixture.Processor().ProcessAsync(Request(scope));

        Assert.Equal(2, fixture.Pending.Query().Count());
        Assert.Equal(2, fixture.Links.Sent.Count);
        Assert.NotEqual(fixture.Links.Sent[0].Token, fixture.Links.Sent[1].Token);
    }

    [UnitFact]
    public async Task GivenAnAddressHeldByADeletedGoogleUser_WhenProcessing_ThenNothingIsWrittenOrSent()
    {
        // The address stays blocked (FR-GO-07), but a deleted Google holder could not act on a notice.
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        await fixture.GoogleUsers.CreateAsync(new GoogleUser
        {
            PublicId = Guid.NewGuid(), GoogleId = "sub", Email = "new@test.local", ScopeId = scope.Id,
            IsDeleted = true
        });

        await fixture.Processor().ProcessAsync(Request(scope));

        Assert.Empty(fixture.Pending.Query().ToList());
        Assert.Empty(fixture.Links.Sent);
        Assert.Empty(fixture.Attempts.Sent);
    }

    [UnitFact]
    public async Task GivenAnAddressHeldByAUserInAnotherCase_WhenProcessing_ThenNothingIsCreatedAndTheOwnerIsTold()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        await fixture.UserAsync(scope, "New@Test.Local");

        await fixture.Processor().ProcessAsync(Request(scope));

        Assert.Single(fixture.Persons.Query().ToList());
        Assert.Empty(fixture.Pending.Query().ToList());
        Assert.Empty(fixture.Links.Sent);
        // The notice goes to the address the holder registered with, not the one the stranger typed.
        Assert.Equal(("New@Test.Local", false), Assert.Single(fixture.Attempts.Sent));
    }

    [UnitFact]
    public async Task GivenAnAddressHeldByAGoogleUser_WhenProcessing_ThenTheOwnerIsToldToUseGoogle()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        await fixture.GoogleUsers.CreateAsync(new GoogleUser
        {
            PublicId = Guid.NewGuid(), GoogleId = "sub", Email = "New@Test.Local", ScopeId = scope.Id
        });

        await fixture.Processor().ProcessAsync(Request(scope));

        Assert.Empty(fixture.Persons.Query().ToList());
        Assert.Empty(fixture.Pending.Query().ToList());
        Assert.Empty(fixture.Links.Sent);
        Assert.Equal(("New@Test.Local", true), Assert.Single(fixture.Attempts.Sent));
    }

    [UnitFact]
    public async Task GivenAnAddressHeldInAnotherScope_WhenProcessing_ThenAPendingRegistrationIsWritten()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        var other = await fixture.ScopeAsync();
        await fixture.UserAsync(other, "new@test.local");

        await fixture.Processor().ProcessAsync(Request(scope));

        Assert.Single(fixture.Persons.Query().ToList());
        Assert.Single(fixture.Pending.Query().ToList());
        Assert.Single(fixture.Links.Sent);
    }

    [UnitFact]
    public async Task GivenAnAddressHeldByARestrictedUser_WhenProcessing_ThenNothingIsCreatedOrSent()
    {
        // NFR-24: mailing a restricted identity would be processing it.
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        await fixture.UserAsync(scope, "new@test.local", restrictedAt: DateTime.UtcNow.AddDays(-1));

        await fixture.Processor().ProcessAsync(Request(scope));

        Assert.Single(fixture.Persons.Query().ToList());
        Assert.Empty(fixture.Pending.Query().ToList());
        Assert.Empty(fixture.Links.Sent);
        Assert.Empty(fixture.Attempts.Sent);
    }

    [UnitFact]
    public async Task GivenTheScopeClosedAfterTheRequestWasQueued_WhenProcessing_ThenNothingHappens()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync(open: false);

        await fixture.Processor().ProcessAsync(Request(scope));

        Assert.Empty(fixture.Persons.Query().ToList());
        Assert.Empty(fixture.Pending.Query().ToList());
        Assert.Empty(fixture.Links.Sent);
        Assert.Empty(fixture.Attempts.Sent);
    }

    [UnitFact]
    public async Task GivenTheScopeDeletedAfterTheRequestWasQueued_WhenProcessing_ThenNothingHappens()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync(isDeleted: true);

        await fixture.Processor().ProcessAsync(Request(scope));

        Assert.Empty(fixture.Persons.Query().ToList());
        Assert.Empty(fixture.Pending.Query().ToList());
        Assert.Empty(fixture.Links.Sent);
        Assert.Empty(fixture.Attempts.Sent);
    }

    [UnitFact]
    public async Task GivenThePendingRegistrationCannotBeStored_WhenProcessing_ThenOneErrorIsLoggedWithoutTheAddressAndNoLinkIsSent()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        var pending = new FailingPendingRepository();
        var logger = new RecordingLogger<RegistrationProcessor>();

        var exception = await Record.ExceptionAsync(() =>
            fixture.Processor(pending, logger).ProcessAsync(Request(scope)));

        // The caller already has the uniform 202, so nothing is thrown; the log line is the only trace,
        // and it must not carry the address (NFR-22).
        Assert.Null(exception);
        Assert.Equal(1, pending.Attempts);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.DoesNotContain("new@test.local", entry.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Links.Sent);
        Assert.Empty(fixture.Attempts.Sent);
    }

    [UnitFact]
    public async Task GivenAScopeWithAVerificationLink_WhenProcessing_ThenTheLinkIsMailedWithThatBase()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync(emailVerificationUrl: "https://fortuna.test/verify-email");

        await fixture.Processor().ProcessAsync(Request(scope));

        Assert.Equal("https://fortuna.test/verify-email", Assert.Single(fixture.Links.LinkBases));
    }

    [UnitFact]
    public async Task GivenAScopeWithNoVerificationLink_WhenProcessing_ThenTheLinkIsMailedWithNoBase()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();

        await fixture.Processor().ProcessAsync(Request(scope));

        Assert.Null(Assert.Single(fixture.Links.LinkBases));
    }

    // FR-SR-13: at most three live pending registrations per scope and address.

    [UnitFact]
    public async Task GivenThreeLiveRegistrationsForTheAddress_WhenProcessing_ThenNoFourthIsWrittenOrMailed()
    {
        // Given three live registrations, one of them in another case
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        await fixture.PendingAsync(scope, "new@test.local", DateTime.UtcNow.AddHours(1));
        await fixture.PendingAsync(scope, "new@test.local", DateTime.UtcNow.AddHours(2));
        await fixture.PendingAsync(scope, "NEW@Test.Local", DateTime.UtcNow.AddHours(3));

        // When
        await fixture.Processor().ProcessAsync(Request(scope));

        // Then
        Assert.Equal(3, fixture.Pending.Query().Count());
        Assert.Empty(fixture.Links.Sent);
    }

    [UnitFact]
    public async Task GivenTwoLiveRegistrationsForTheAddress_WhenProcessing_ThenAThirdIsWrittenAndMailed()
    {
        // Given
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        await fixture.PendingAsync(scope, "new@test.local", DateTime.UtcNow.AddHours(1));
        await fixture.PendingAsync(scope, "new@test.local", DateTime.UtcNow.AddHours(2));

        // When
        await fixture.Processor().ProcessAsync(Request(scope));

        // Then
        Assert.Equal(3, fixture.Pending.Query().Count());
        Assert.Single(fixture.Links.Sent);
    }

    [UnitFact]
    public async Task GivenThreeExpiredRegistrationsForTheAddress_WhenProcessing_ThenANewOneIsWrittenAndMailed()
    {
        // Given three that expired and await the purge — they hold nothing and mail nothing more
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();

        for (var i = 1; i <= 3; i++)
        {
            await fixture.PendingAsync(scope, "new@test.local", DateTime.UtcNow.AddMinutes(-i));
        }

        // When
        await fixture.Processor().ProcessAsync(Request(scope));

        // Then
        Assert.Single(fixture.Links.Sent);
    }

    [UnitFact]
    public async Task GivenThreeLiveRegistrationsForTheAddressInAnotherScope_WhenProcessing_ThenOneIsWrittenHere()
    {
        // Given — the cap is per scope, as the address's uniqueness is
        var fixture = new Fixture();
        var other = await fixture.ScopeAsync();
        var scope = await fixture.ScopeAsync();

        for (var i = 1; i <= 3; i++)
        {
            await fixture.PendingAsync(other, "new@test.local", DateTime.UtcNow.AddHours(i));
        }

        // When
        await fixture.Processor().ProcessAsync(Request(scope));

        // Then
        Assert.Single(fixture.Pending.Query().Where(x => x.ScopeId == scope.Id).ToList());
        Assert.Single(fixture.Links.Sent);
    }

    // FR-SR-14: at most three attempt notices per address a day, whichever scope was named.

    [UnitFact]
    public async Task GivenAHolderAlreadyToldThreeTimes_WhenProcessingAFourthAttempt_ThenNoNoticeIsSent()
    {
        // Given
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        await fixture.UserAsync(scope, "taken@test.local");
        var processor = fixture.Processor();

        // When four attempts are processed
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await processor.ProcessAsync(Request(scope, "taken@test.local"));
        }

        // Then
        Assert.Equal(3, fixture.Attempts.Sent.Count);
        Assert.Empty(fixture.Pending.Query().ToList());
    }

    [UnitFact]
    public async Task GivenOneAddressHeldInTwoScopes_WhenAttemptsNameBoth_ThenThreeNoticesInAllAreSent()
    {
        // Given — the budget belongs to the inbox, not to the account in one scope
        var fixture = new Fixture();
        var first = await fixture.ScopeAsync();
        var second = await fixture.ScopeAsync();
        await fixture.UserAsync(first, "taken@test.local");
        await fixture.UserAsync(second, "Taken@Test.Local");
        var processor = fixture.Processor();

        // When
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await processor.ProcessAsync(Request(first, "taken@test.local"));
            await processor.ProcessAsync(Request(second, "taken@test.local"));
        }

        // Then
        Assert.Equal(3, fixture.Attempts.Sent.Count);
        Assert.Equal(3, fixture.NoticeBudgets.SentTo(AttemptNoticeBudget.AddressHashOf("taken@test.local")));
    }

    [UnitFact]
    public async Task GivenAGoogleHolderAlreadyToldThreeTimes_WhenProcessingAFourthAttempt_ThenNoNoticeIsSent()
    {
        // Given
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        await fixture.GoogleUsers.CreateAsync(new GoogleUser
        {
            PublicId = Guid.NewGuid(), GoogleId = "google-1", Name = "Google", Email = "google@test.local",
            EmailVerified = true, ScopeId = scope.Id
        });
        var processor = fixture.Processor();

        // When
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await processor.ProcessAsync(Request(scope, "google@test.local"));
        }

        // Then
        Assert.Equal(3, fixture.Attempts.Sent.Count);
        Assert.All(fixture.Attempts.Sent, sent => Assert.True(sent.GoogleAccount));
    }

    [UnitFact]
    public async Task GivenARestrictedHolder_WhenProcessingAnAttempt_ThenNothingIsChargedToTheAddress()
    {
        // Given — no notice is sent to a restricted holder (NFR-24), so none is counted either
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        await fixture.UserAsync(scope, "restricted@test.local", DateTime.UtcNow);

        // When
        await fixture.Processor().ProcessAsync(Request(scope, "restricted@test.local"));

        // Then
        Assert.Equal(0, fixture.NoticeBudgets.SentTo(AttemptNoticeBudget.AddressHashOf("restricted@test.local")));
    }

    [UnitFact]
    public async Task GivenAnAddressInAnyCaseOrSpacing_WhenItsDigestIsTaken_ThenItIsTheSameDigest()
    {
        // Given / When
        var plain = AttemptNoticeBudget.AddressHashOf("taken@test.local");
        var varied = AttemptNoticeBudget.AddressHashOf("  Taken@TEST.local ");

        // Then — one inbox, one budget; and the digest is 64 hex characters, never the address
        Assert.Equal(plain, varied);
        Assert.Equal(64, plain.Length);
        Assert.DoesNotContain("test.local", plain);
    }
}
