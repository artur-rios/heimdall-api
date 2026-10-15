using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for EmailVerificationTokenRetirement (UC-15 step 3, UC-51): the shared pass that marks a
// person's live verification tokens used, leaving expired and already-used tokens and other people's
// tokens alone.
public class EmailVerificationTokenRetirementTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static EmailVerificationToken Token(long personId, DateTime expiresAt, bool used = false) => new()
    {
        PersonId = personId,
        TokenHash = SingleUseTokenHash.Of(Guid.NewGuid().ToString("N")),
        ExpiresAt = expiresAt,
        Used = used
    };

    [UnitFact]
    public async Task GivenLiveTokens_WhenRetiring_ThenTheyAreMarkedUsed()
    {
        // Given
        var tokens = new AsyncFakeRepository<EmailVerificationToken, long>();
        var first = Token(1, Now.AddHours(1));
        var second = Token(1, Now.AddMinutes(5));
        await tokens.CreateAsync(first);
        await tokens.CreateAsync(second);

        // When
        var errors = await EmailVerificationTokenRetirement.RetireLiveAsync(tokens, tokens, 1, Now);

        // Then
        Assert.Null(errors);
        Assert.True(first.Used);
        Assert.True(second.Used);
    }

    [UnitFact]
    public async Task GivenExpiredAndUsedTokens_WhenRetiring_ThenTheyAreLeftAlone()
    {
        // Given
        var tokens = new AsyncFakeRepository<EmailVerificationToken, long>();
        var expired = Token(1, Now.AddMinutes(-1));
        var used = Token(1, Now.AddHours(1), used: true);
        await tokens.CreateAsync(expired);
        await tokens.CreateAsync(used);

        // When
        var errors = await EmailVerificationTokenRetirement.RetireLiveAsync(tokens, tokens, 1, Now);

        // Then
        Assert.Null(errors);
        Assert.False(expired.Used);
        Assert.True(used.Used);
    }

    [UnitFact]
    public async Task GivenAnotherPersonsToken_WhenRetiring_ThenItIsNotTouched()
    {
        // Given
        var tokens = new AsyncFakeRepository<EmailVerificationToken, long>();
        var others = Token(2, Now.AddHours(1));
        await tokens.CreateAsync(others);

        // When
        var errors = await EmailVerificationTokenRetirement.RetireLiveAsync(tokens, tokens, 1, Now);

        // Then
        Assert.Null(errors);
        Assert.False(others.Used);
    }
}
