using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for PasswordResetService (UC-12 steps 3 and 4, FR-PR-02): a token is persisted for the
// person, unused and with a future expiry, then handed to the sender.
//
// Since TH-14 the token itself is not persisted — only its SHA-256 — so every property of the token
// is now asserted against what the *sender* was handed, which is the only place the plaintext exists
// after issue. That is also how a caller obtains it: out of an email. A test that read it back from
// the repository would be testing a system where the fix had not been made.
public class PasswordResetServiceTests
{
    private static async Task<Person> PersonAsync()
    {
        var person = new Person { Email = "user@test.local" };

        await new AsyncFakeRepository<Person>().CreateAsync(person); // assigns person.Id

        return person;
    }

    /// <summary>
    ///     A sender that records every token it is asked to deliver, in order.
    /// </summary>
    private static Mock<IPasswordResetSender> RecordingSender(List<string> delivered)
    {
        var sender = new Mock<IPasswordResetSender>();

        sender.Setup(s => s.SendAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, token) => delivered.Add(token))
            .Returns(Task.CompletedTask);

        return sender;
    }

    [UnitFact]
    public async Task GivenAPerson_WhenIssuingAndSending_ThenTokenIsPersistedAndSent()
    {
        // Given
        var delivered = new List<string>();
        var tokens = new AsyncFakeRepository<PasswordResetToken>();
        var sender = RecordingSender(delivered);
        var options = new PasswordResetOptions { TokenLifetime = TimeSpan.FromHours(1) };
        var service = new PasswordResetService(tokens, sender.Object, options);
        var person = await PersonAsync();

        // When
        await service.IssueAndSendAsync(person);

        // Then — a token was stored for the person, unused, expiring in the future
        var stored = (await tokens.GetAllAsync()).Data!.Single();
        Assert.Equal(person.Id, stored.PersonId);
        Assert.False(stored.Used);
        Assert.NotEmpty(stored.TokenHash);
        Assert.True(stored.ExpiresAt > DateTime.UtcNow);

        // Then — the sender received the person's email and the token whose digest was stored
        var sent = Assert.Single(delivered);
        sender.Verify(s => s.SendAsync(person.Email, sent), Times.Once);
        Assert.Equal(SingleUseTokenHash.Of(sent), stored.TokenHash);
    }

    [UnitFact]
    public async Task GivenAPerson_WhenIssuingAndSending_ThenTheTokenItselfIsNotStored()
    {
        // TH-14, stated as an assertion rather than as a comment. What was stored has to be the
        // digest and nothing else: a row that also kept the token, in any column, would let anyone
        // who can read the table complete a reset for the account.
        var delivered = new List<string>();
        var tokens = new AsyncFakeRepository<PasswordResetToken>();
        var service = new PasswordResetService(
            tokens, RecordingSender(delivered).Object, new PasswordResetOptions());

        await service.IssueAndSendAsync(await PersonAsync());

        var stored = (await tokens.GetAllAsync()).Data!.Single();
        var sent = Assert.Single(delivered);

        Assert.Equal(SingleUseTokenHash.Of(sent), stored.TokenHash);
        Assert.NotEqual(sent, stored.TokenHash);
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
        var delivered = new List<string>();
        var tokens = new AsyncFakeRepository<PasswordResetToken>();
        var service = new PasswordResetService(
            tokens, RecordingSender(delivered).Object, new PasswordResetOptions());
        var person = await PersonAsync();

        // When
        await service.IssueAndSendAsync(person);
        await service.IssueAndSendAsync(person);

        // Then
        var stored = (await tokens.GetAllAsync()).Data!.ToList();
        Assert.Equal(2, stored.Count);
        Assert.Equal(2, delivered.Count);
        Assert.NotEqual(delivered[0], delivered[1]);

        // And the digests differ with them, which is what the unique index relies on.
        Assert.NotEqual(stored[0].TokenHash, stored[1].TokenHash);
    }

    [UnitFact]
    public async Task GivenAPerson_WhenIssuingAndSending_ThenTokenIsLongAndUrlSafe()
    {
        // Given / When — the token's secrecy rests on its length, and it travels inside a link in
        // an email, so it must survive a URL
        var delivered = new List<string>();
        var tokens = new AsyncFakeRepository<PasswordResetToken>();
        var service = new PasswordResetService(
            tokens, RecordingSender(delivered).Object, new PasswordResetOptions());

        await service.IssueAndSendAsync(await PersonAsync());

        // Then
        var token = Assert.Single(delivered);
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
