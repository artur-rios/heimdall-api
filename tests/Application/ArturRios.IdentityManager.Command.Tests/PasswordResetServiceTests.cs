using ArturRios.IdentityManager.Command.Services;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for PasswordResetService (UC-12 steps 3 and 4, FR-PR-02): a token is persisted for the
// person, unused and with a future expiry, then handed to the sender.
public class PasswordResetServiceTests
{
    private static async Task<Person> PersonAsync()
    {
        var person = new Person { Email = "user@test.local" };

        await new AsyncFakeRepository<Person>().CreateAsync(person); // assigns person.Id

        return person;
    }

    [UnitFact]
    public async Task GivenAPerson_WhenIssuingAndSending_ThenTokenIsPersistedAndSent()
    {
        // Given
        var tokens = new AsyncFakeRepository<PasswordResetToken>();
        var sender = new Mock<IPasswordResetSender>();
        var options = new PasswordResetOptions { TokenLifetime = TimeSpan.FromHours(1) };
        var service = new PasswordResetService(tokens, sender.Object, options);
        var person = await PersonAsync();

        // When
        await service.IssueAndSendAsync(person);

        // Then — a token was stored for the person, unused, expiring in the future
        var stored = (await tokens.GetAllAsync()).Data!.Single();
        Assert.Equal(person.Id, stored.PersonId);
        Assert.False(stored.Used);
        Assert.False(string.IsNullOrWhiteSpace(stored.Token));
        Assert.True(stored.ExpiresAt > DateTime.UtcNow);

        // Then — the sender received the person's email and the same token
        sender.Verify(s => s.SendAsync(person.Email, stored.Token), Times.Once);
    }

    [UnitFact]
    public async Task GivenAConfiguredLifetime_WhenIssuingAndSending_ThenExpiryHonoursIt()
    {
        // Given a lifetime far from the one-hour default, so the assertion cannot pass by accident
        var tokens = new AsyncFakeRepository<PasswordResetToken>();
        var options = new PasswordResetOptions { TokenLifetime = TimeSpan.FromMinutes(15) };
        var service = new PasswordResetService(tokens, new Mock<IPasswordResetSender>().Object, options);
        var issuedAt = DateTime.UtcNow;

        // When
        await service.IssueAndSendAsync(await PersonAsync());

        // Then — expiry lands a quarter-hour out, allowing for the clock moving during the call
        var stored = (await tokens.GetAllAsync()).Data!.Single();
        Assert.InRange(
            stored.ExpiresAt,
            issuedAt.AddMinutes(15),
            issuedAt.AddMinutes(15).AddSeconds(30));
    }

    [UnitFact]
    public async Task GivenTwoRequests_WhenIssuingAndSending_ThenTokensDiffer()
    {
        // Given a service issuing twice for the same person: the token is what stands between an
        // intercepted mailbox and a changed password, so it must not be predictable from a prior one
        var tokens = new AsyncFakeRepository<PasswordResetToken>();
        var service = new PasswordResetService(
            tokens, new Mock<IPasswordResetSender>().Object, new PasswordResetOptions());
        var person = await PersonAsync();

        // When
        await service.IssueAndSendAsync(person);
        await service.IssueAndSendAsync(person);

        // Then
        var stored = (await tokens.GetAllAsync()).Data!.ToList();
        Assert.Equal(2, stored.Count);
        Assert.NotEqual(stored[0].Token, stored[1].Token);
    }

    [UnitFact]
    public async Task GivenAPerson_WhenIssuingAndSending_ThenTokenIsLongAndUrlSafe()
    {
        // Given / When — the token's secrecy rests on its length, and it travels inside a link in
        // an email, so it must survive a URL
        var tokens = new AsyncFakeRepository<PasswordResetToken>();
        var service = new PasswordResetService(
            tokens, new Mock<IPasswordResetSender>().Object, new PasswordResetOptions());

        await service.IssueAndSendAsync(await PersonAsync());

        // Then
        var token = (await tokens.GetAllAsync()).Data!.Single().Token;
        Assert.Equal(48, token.Length);

        // The service asks CustomRandom for letters and digits. Before ArturRios.Util 1.5.0 that
        // request was only half honoured — the helper padded from its full alphabet — so this
        // assertion is what proves the fixed version is the one being resolved. Escaping in the
        // sender means nothing breaks if it ever regresses, but a regression should be caught here
        // rather than tolerated.
        Assert.All(token, character => Assert.True(
            char.IsAsciiLetterOrDigit(character),
            $"token contains the non-alphanumeric character '{character}'"));
    }
}
