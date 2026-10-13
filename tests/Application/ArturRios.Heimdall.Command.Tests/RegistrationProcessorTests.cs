using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for RegistrationProcessor (UC-47 steps 4 to 6, run after the caller has been answered).
// What separates the flows is what was created and what was mailed, so each test asserts both.
public class RegistrationProcessorTests
{
    private sealed class RecordingVerificationSender : IEmailVerificationSender
    {
        public List<(string Email, string Token)> Sent { get; } = [];

        public Task SendAsync(string email, string token)
        {
            Sent.Add((email, token));
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

    private sealed class Fixture
    {
        public AsyncFakeRepository<Scope, long> Scopes { get; } = new();
        public AsyncFakeRepository<Person, long> Persons { get; } = new();
        public AsyncFakeRepository<GoogleUser, long> GoogleUsers { get; } = new();
        public AsyncFakeRepository<PendingRegistration, long> Pending { get; } = new();
        public RecordingVerificationSender Links { get; } = new();
        public RecordingAttemptSender Attempts { get; } = new();
        public EmailVerificationOptions Options { get; } = new() { TokenLifetime = TimeSpan.FromHours(24) };

        public RegistrationProcessor Processor() =>
            new(Scopes, Persons, GoogleUsers, Pending, Links, Attempts, Options,
                NullLogger<RegistrationProcessor>.Instance);

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
        Assert.Equal(("new@test.local", false), Assert.Single(fixture.Attempts.Sent));
    }

    [UnitFact]
    public async Task GivenAnAddressHeldByAGoogleUser_WhenProcessing_ThenTheOwnerIsToldToUseGoogle()
    {
        var fixture = new Fixture();
        var scope = await fixture.ScopeAsync();
        await fixture.GoogleUsers.CreateAsync(new GoogleUser
        {
            PublicId = Guid.NewGuid(), GoogleId = "sub", Email = "new@test.local", ScopeId = scope.Id
        });

        await fixture.Processor().ProcessAsync(Request(scope));

        Assert.Empty(fixture.Persons.Query().ToList());
        Assert.Empty(fixture.Pending.Query().ToList());
        Assert.Empty(fixture.Links.Sent);
        Assert.Equal(("new@test.local", true), Assert.Single(fixture.Attempts.Sent));
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
}
