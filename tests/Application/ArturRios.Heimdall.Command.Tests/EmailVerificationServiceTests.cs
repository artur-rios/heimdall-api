using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for EmailVerificationService (UC-06, FR-EV-01/02): a token is persisted for the person
// with a future expiry and Used=false, then handed to the sender.
//
// As in PasswordResetServiceTests, the token's own properties are asserted against what the sender
// was handed rather than against the stored row: since TH-14 the row keeps only a SHA-256, and the
// plaintext exists nowhere else once the method returns.
public class EmailVerificationServiceTests
{
    [UnitFact]
    public async Task GivenAPerson_WhenIssuingAndSending_ThenTokenIsPersistedAndSent()
    {
        // Given
        var delivered = new List<string>();
        var tokens = new AsyncFakeRepository<EmailVerificationToken>();
        var sender = new Mock<IEmailVerificationSender>();

        sender.Setup(s => s.SendAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, token) => delivered.Add(token))
            .Returns(Task.CompletedTask);

        var options = new EmailVerificationOptions { TokenLifetime = TimeSpan.FromHours(1) };
        var service = new EmailVerificationService(tokens, sender.Object, options);
        var person = new Person { Email = "user@test.local" };
        await new AsyncFakeRepository<Person>().CreateAsync(person); // assigns person.Id

        // When
        await service.IssueAndSendAsync(person);

        // Then — a token was stored for the person, unused, expiring in the future
        var stored = (await tokens.GetAllAsync()).Data!.Single();
        Assert.Equal(person.Id, stored.PersonId);
        Assert.False(stored.Used);
        Assert.NotEmpty(stored.TokenHash);
        Assert.True(stored.ExpiresAt > DateTime.UtcNow);

        // Then — alphanumeric, so it survives the link it is delivered in. Same request and same
        // guarantee as PasswordResetService, and only honoured from ArturRios.Util 1.5.0 onwards.
        var sent = Assert.Single(delivered);

        Assert.All(sent, character => Assert.True(
            char.IsAsciiLetterOrDigit(character),
            $"token contains the non-alphanumeric character '{character}'"));

        // Then — the sender received the person's email, and what was stored is that token's digest
        // rather than the token (TH-14)
        sender.Verify(s => s.SendAsync(person.Email, sent), Times.Once);
        Assert.Equal(SingleUseTokenHash.Of(sent), stored.TokenHash);
    }
}
