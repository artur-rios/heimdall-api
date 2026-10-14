using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Domain.Tests;

// Unit tests for ApplicationCredential's liveness rule (UC-53…UC-57, FR-AP-10): a credential is live
// while it is not revoked and either has no expiry or expires after the moment asked about. The rule
// is written once, as an expression EF can translate, and IsLiveAt evaluates that same expression in
// memory, so these cases pin both.
public class ApplicationCredentialTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    [UnitFact]
    public void GivenNoExpiryAndNotRevoked_WhenAskedIfLive_ThenItIs()
    {
        // Given
        var credential = new ApplicationCredential();

        // When
        var live = credential.IsLiveAt(Now);

        // Then
        Assert.True(live);
    }

    [UnitFact]
    public void GivenAnExpiryAfterNow_WhenAskedIfLive_ThenItIs()
    {
        // Given
        var credential = new ApplicationCredential { ExpiresAt = Now.AddSeconds(1) };

        // When
        var live = credential.IsLiveAt(Now);

        // Then
        Assert.True(live);
    }

    [UnitFact]
    public void GivenAnExpiryAtExactlyNow_WhenAskedIfLive_ThenItIsNot()
    {
        // Given — the boundary belongs to "expired", as for every token in this API
        var credential = new ApplicationCredential { ExpiresAt = Now };

        // When
        var live = credential.IsLiveAt(Now);

        // Then
        Assert.False(live);
    }

    [UnitFact]
    public void GivenARevokedCredentialWithoutExpiry_WhenAskedIfLive_ThenItIsNot()
    {
        // Given
        var credential = new ApplicationCredential { RevokedAt = Now.AddDays(-1) };

        // When
        var live = credential.IsLiveAt(Now);

        // Then
        Assert.False(live);
    }

    [UnitFact]
    public void GivenSeveralCredentials_WhenFilteredByTheExpression_ThenOnlyLiveOnesRemain()
    {
        // Given
        var live = new ApplicationCredential { ExpiresAt = Now.AddHours(1) };
        var expired = new ApplicationCredential { ExpiresAt = Now.AddHours(-1) };
        var revoked = new ApplicationCredential { RevokedAt = Now.AddHours(-1) };

        // When
        var remaining = new[] { live, expired, revoked }.AsQueryable()
            .Where(ApplicationCredential.LiveAt(Now))
            .ToList();

        // Then
        Assert.Equal([live], remaining);
    }
}
