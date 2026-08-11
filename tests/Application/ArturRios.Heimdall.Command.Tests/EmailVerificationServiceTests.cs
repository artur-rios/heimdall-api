using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for EmailVerificationService (UC-06, FR-EV-01/02): a token is persisted for the person
// with a future expiry and Used=false, then handed to the sender.
public class EmailVerificationServiceTests
{
    [UnitFact]
    public async Task GivenAPerson_WhenIssuingAndSending_ThenTokenIsPersistedAndSent()
    {
        // Given
        var tokens = new AsyncFakeRepository<EmailVerificationToken>();
        var sender = new Mock<IEmailVerificationSender>();
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
        Assert.False(string.IsNullOrWhiteSpace(stored.Token));
        Assert.True(stored.ExpiresAt > DateTime.UtcNow);

        // Then — alphanumeric, so it survives the link it is delivered in. Same request and same
        // guarantee as PasswordResetService, and only honoured from ArturRios.Util 1.5.0 onwards.
        Assert.All(stored.Token, character => Assert.True(
            char.IsAsciiLetterOrDigit(character),
            $"token contains the non-alphanumeric character '{character}'"));

        // Then — the sender received the person's email and the same token
        sender.Verify(s => s.SendAsync(person.Email, stored.Token), Times.Once);
    }
}
