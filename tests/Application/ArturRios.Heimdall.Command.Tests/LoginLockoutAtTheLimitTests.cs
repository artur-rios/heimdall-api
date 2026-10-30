using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Command.Tests.Support;
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

// Unit tests for the lockout threshold of LoginCommandHandler (FR-AU-09, UC-11 AF-11b): the tenth
// attempt is the one that locks the account, and it locks it when the attempt is reserved, not when
// the password comparison finishes. The case that matters is a request that reserves the tenth
// attempt and never finishes comparing — the hash gate sheds it — because the account must then be
// locked for the usual period rather than left in a state no later request can leave.
//
// The shed case swaps PasswordHashGate.Shared, so this class is in the non-parallel collection.
[Collection(PasswordHashGateCollection.Name)]
public class LoginLockoutAtTheLimitTests
{
    private const string Email = "admin@test.local";
    private const string Password = "Str0ngPass!";

    private static Person AdminAt(int failedAttempts)
    {
        return new Person
        {
            Id = 10,
            PublicId = Guid.NewGuid(),
            Name = "person-10",
            Email = Email,
            PasswordHash = Hash.EncodeWithRandomSalt(Password, out var salt, PasswordHashing.Configuration),
            Salt = salt,
            RoleId = (long)Roles.SystemAdmin,
            FailedLoginAttempts = failedAttempts
        };
    }

    private static LoginCommandHandler HandlerFor(AsyncFakeRepository<Person, long> persons)
    {
        var validator = new Mock<IValidator<LoginCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<LoginCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        var emailCodes = new AsyncFakeRepository<TwoFactorEmailCode, long>();

        return new LoginCommandHandler(
            validator.Object,
            persons,
            new AsyncFakeRepository<TwoFactorAuth, long>(),
            InMemoryAtomicWrites.Instance,
            new TwoFactorEmailCodeIssuer(emailCodes, emailCodes, Mock.Of<ITwoFactorEmailSender>()),
            Mock.Of<ITwoFactorChallengeTokenIssuer>(),
            new PersonAuthTokenService(new StubIssuer()));
    }

    private static LoginCommand Command(string password = Password) =>
        new() { Email = Email, Password = password };

    private sealed class StubIssuer : IAuthTokenIssuer
    {
        public Task<AuthToken> IssueAsync(AuthTokenSubject subject) =>
            Task.FromResult(new AuthToken("issued-token", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        public Task<AuthToken> IssueSubjectRightsOnlyAsync(Guid subjectId, int roleId, Guid? securityStamp) =>
            Task.FromResult(new AuthToken("rights-only-token", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [UnitFact]
    public async Task GivenTheGateShedsTheTenthAttempt_WhenHandlingLogin_ThenTheAccountIsLockedAndUnlocksByItself()
    {
        // Given nine failures, and a hash gate that has no permit left for the tenth attempt
        var person = AdminAt(9);
        var persons = new AsyncFakeRepository<Person, long>();
        await persons.CreateAsync(person);
        var handler = HandlerFor(persons);

        var release = new TaskCompletionSource();
        var holding = new TaskCompletionSource();
        var original = PasswordHashGate.Shared;
        var saturated = new PasswordHashGate(1, TimeSpan.FromMilliseconds(50));

        PasswordHashGate.Shared = saturated;

        var holder = saturated.RunAsync(async () =>
        {
            holding.SetResult();

            await release.Task;
        });

        try
        {
            await holding.Task;

            // When the tenth attempt reserves its slot and is then shed before the comparison
            await Assert.ThrowsAsync<PasswordHashGateSaturatedException>(() => handler.HandleAsync(Command()));
        }
        finally
        {
            release.SetResult();
            await holder;

            PasswordHashGate.Shared = original;
        }

        // Then the account is locked for a period, with the counter folded into the lock
        var stored = Assert.Single(persons.Query().ToList());
        Assert.NotNull(stored.LockedOutUntil);
        Assert.True(stored.LockedOutUntil > DateTime.UtcNow);
        Assert.Equal(0, stored.FailedLoginAttempts);

        // Then — the correct password is refused while the lock holds
        Assert.False((await handler.HandleAsync(Command())).Success);

        // Then — and once the lock has expired, the correct password signs in
        stored.LockedOutUntil = DateTime.UtcNow.AddMinutes(-1);

        var output = await handler.HandleAsync(Command());

        Assert.True(output.Success);
        Assert.Equal(0, stored.FailedLoginAttempts);
        Assert.Null(stored.LockedOutUntil);
    }

    [UnitFact]
    public async Task GivenNineFailures_WhenTheTenthAttemptIsTheCorrectPassword_ThenItSignsInAndClearsBothFields()
    {
        // Given nine failures: the tenth reservation reaches the limit and locks, but a right
        // password on the last allowed attempt is still a sign-in
        var persons = new AsyncFakeRepository<Person, long>();
        await persons.CreateAsync(AdminAt(9));

        // When
        var output = await HandlerFor(persons).HandleAsync(Command());

        // Then
        Assert.True(output.Success);

        var stored = Assert.Single(persons.Query().ToList());
        Assert.Equal(0, stored.FailedLoginAttempts);
        Assert.Null(stored.LockedOutUntil);
    }

    [UnitFact]
    public async Task GivenNineFailures_WhenTheTenthAttemptIsWrong_ThenTheAccountIsLockedWithTheCounterReset()
    {
        // Given
        var persons = new AsyncFakeRepository<Person, long>();
        await persons.CreateAsync(AdminAt(9));

        // When
        var output = await HandlerFor(persons).HandleAsync(Command("wrong-password"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.InvalidCredentials, output.Errors);

        var stored = Assert.Single(persons.Query().ToList());
        Assert.NotNull(stored.LockedOutUntil);
        Assert.True(stored.LockedOutUntil > DateTime.UtcNow);
        Assert.Equal(0, stored.FailedLoginAttempts);
    }

    [UnitFact]
    public async Task GivenARowStuckAtTheLimitWithNoLock_WhenHandlingLogin_ThenItIsLockedAndUnlocksByItself()
    {
        // Given a row an older instance or a lowered limit left at the limit with no lockout date
        var person = AdminAt(10);
        var persons = new AsyncFakeRepository<Person, long>();
        await persons.CreateAsync(person);
        var handler = HandlerFor(persons);

        // When a wrong password is tried
        var output = await handler.HandleAsync(Command("wrong-password"));

        // Then the reservation locks it and resets the counter
        Assert.False(output.Success);
        Assert.NotNull(person.LockedOutUntil);
        Assert.True(person.LockedOutUntil > DateTime.UtcNow);
        Assert.Equal(0, person.FailedLoginAttempts);

        // Then — and once the lock has expired, the correct password signs in
        person.LockedOutUntil = DateTime.UtcNow.AddMinutes(-1);

        Assert.True((await handler.HandleAsync(Command())).Success);
        Assert.Null(person.LockedOutUntil);
    }
}
