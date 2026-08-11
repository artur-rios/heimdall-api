using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.WebApi.Security.Constants;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for IdentityUserMapper (UC-11, FR-AU-04). One class owns writing and reading the token
// claims, so the property that matters is the round trip: whatever ToClaims writes, FromClaims must
// give back. Reading is also total — a claim set it cannot interpret yields null, never an
// exception, so a malformed token is rejected as unauthenticated rather than failing with a 500.
public class IdentityUserMapperTests
{
    private static readonly IdentityUserMapper Mapper = new();

    [UnitFact]
    public void GivenUserIdentity_WhenRoundTrippingClaims_ThenScopeIsPreserved()
    {
        // Given
        var user = new IdentityUser(Guid.NewGuid(), (int)Roles.User, Guid.NewGuid(), []);

        // When
        var restored = Mapper.FromClaims(Mapper.ToClaims(user));

        // Then
        Assert.Equal(user, restored);
    }

    [UnitFact]
    public void GivenScopeAdminIdentity_WhenRoundTrippingClaims_ThenOwnedScopesArePreservedInOrder()
    {
        // Given a ScopeAdmin owning several scopes, which travel as one comma-separated claim
        var owned = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var user = new IdentityUser(Guid.NewGuid(), (int)Roles.ScopeAdmin, null, owned);

        // When
        var restored = (IdentityUser)Mapper.FromClaims(Mapper.ToClaims(user))!;

        // Then
        Assert.Null(restored.ScopeId);
        Assert.Equal(owned, restored.OwnedScopeIds);
    }

    [UnitFact]
    public void GivenSystemAdminIdentity_WhenWritingClaims_ThenNoScopeClaimIsEmitted()
    {
        // Given a SystemAdmin, who belongs to no scope
        var user = new IdentityUser(Guid.NewGuid(), (int)Roles.SystemAdmin);

        // When
        var claims = Mapper.ToClaims(user);

        // Then — the claims are absent, not empty: a token never suggests an association the person
        // does not have
        Assert.False(claims.ContainsKey(IdentityUserMapper.ScopeIdClaim));
        Assert.False(claims.ContainsKey(IdentityUserMapper.OwnedScopeIdsClaim));
        Assert.Equal(user, Mapper.FromClaims(claims));
    }

    [UnitFact]
    public void GivenIdentity_WhenWritingClaims_ThenNoValueIsAnInternalId()
    {
        // Given — NFR-15: every identifier in a token is a PublicId
        var user = new IdentityUser(
            Guid.NewGuid(), (int)Roles.ScopeAdmin, null, [Guid.NewGuid()]);

        // When
        var claims = Mapper.ToClaims(user);

        // Then every claim but the role parses as a GUID, so none of them is a bigint id
        foreach (var (key, value) in claims.Where(claim => claim.Key != TokenClaimKeys.RoleId))
        {
            Assert.All(
                value.Split(','),
                entry => Assert.True(Guid.TryParse(entry, out _), $"claim '{key}' is not a GUID"));
        }
    }

    [UnitFact]
    public void GivenNoClaims_WhenReadingClaims_ThenReturnsNull()
    {
        // Given / When
        var restored = Mapper.FromClaims(new Dictionary<string, string>());

        // Then
        Assert.Null(restored);
    }

    [UnitTheory]
    [InlineData("not-a-guid", "3")]
    [InlineData("", "3")]
    public void GivenUnparseableIdClaim_WhenReadingClaims_ThenReturnsNull(string id, string role)
    {
        // Given a token whose id claim is not a GUID
        var claims = new Dictionary<string, string>
        {
            [TokenClaimKeys.Id] = id, [TokenClaimKeys.RoleId] = role
        };

        // When / Then
        Assert.Null(Mapper.FromClaims(claims));
    }

    [UnitTheory]
    [InlineData("not-a-number")]
    [InlineData("")]
    [InlineData("99")]
    public void GivenUnusableRoleClaim_WhenReadingClaims_ThenReturnsNull(string role)
    {
        // Given a role claim that is not a number, or is a number that names no role — a token
        // claiming role 99 must not authenticate anyone
        var claims = new Dictionary<string, string>
        {
            [TokenClaimKeys.Id] = Guid.NewGuid().ToString(), [TokenClaimKeys.RoleId] = role
        };

        // When / Then
        Assert.Null(Mapper.FromClaims(claims));
    }

    [UnitFact]
    public void GivenMalformedScopeClaim_WhenReadingClaims_ThenIdentityIsReadWithoutIt()
    {
        // Given a valid identity carrying an unparseable scope claim
        var id = Guid.NewGuid();
        var claims = new Dictionary<string, string>
        {
            [TokenClaimKeys.Id] = id.ToString(),
            [TokenClaimKeys.RoleId] = ((int)Roles.User).ToString(),
            [IdentityUserMapper.ScopeIdClaim] = "not-a-guid"
        };

        // When
        var restored = (IdentityUser)Mapper.FromClaims(claims)!;

        // Then — the caller is still identified, but claims no scope, so scope-based authorization
        // denies rather than granting on a value nobody could read
        Assert.Equal(id, restored.Id);
        Assert.Null(restored.ScopeId);
    }

    [UnitFact]
    public void GivenPartlyMalformedOwnedScopeClaim_WhenReadingClaims_ThenOnlyReadableScopesSurvive()
    {
        // Given one readable and one unreadable owned scope id
        var readable = Guid.NewGuid();
        var claims = new Dictionary<string, string>
        {
            [TokenClaimKeys.Id] = Guid.NewGuid().ToString(),
            [TokenClaimKeys.RoleId] = ((int)Roles.ScopeAdmin).ToString(),
            [IdentityUserMapper.OwnedScopeIdsClaim] = $"{readable},not-a-guid"
        };

        // When
        var restored = (IdentityUser)Mapper.FromClaims(claims)!;

        // Then
        Assert.Equal([readable], restored.OwnedScopeIds);
    }

    [UnitFact]
    public void GivenIdentityWithScopePermissionClaims_WhenRoundTrippingClaims_ThenClaimsArePreserved()
    {
        // Given an identity carrying the names of its scope's flagged permissions
        var user = new IdentityUser(Guid.NewGuid(), (int)Roles.User, Guid.NewGuid(), [])
        {
            ScopePermissionClaims = ["documents.read", "documents.write"]
        };

        // When
        var restored = (IdentityUser)Mapper.FromClaims(Mapper.ToClaims(user))!;

        // Then — the permission names survive the round trip, in order
        Assert.Equal(user.ScopePermissionClaims, restored.ScopePermissionClaims);
    }

    [UnitFact]
    public void GivenIdentityWithNoScopePermissionClaims_WhenWritingClaims_ThenNoPermissionClaimIsEmitted()
    {
        // Given an identity with no flagged permissions
        var user = new IdentityUser(Guid.NewGuid(), (int)Roles.SystemAdmin);

        // When
        var claims = Mapper.ToClaims(user);

        // Then — the claim is absent, not empty, so a token never claims permissions the caller lacks
        Assert.False(claims.ContainsKey(IdentityUserMapper.ScopePermissionClaimsClaim));
    }

    [UnitFact]
    public void GivenMalformedScopePermissionClaim_WhenReadingClaims_ThenIdentityIsReadWithoutIt()
    {
        // Given a valid identity carrying an unparseable permission claim
        var id = Guid.NewGuid();
        var claims = new Dictionary<string, string>
        {
            [TokenClaimKeys.Id] = id.ToString(),
            [TokenClaimKeys.RoleId] = ((int)Roles.User).ToString(),
            [IdentityUserMapper.ScopePermissionClaimsClaim] = "not-json"
        };

        // When
        var restored = (IdentityUser)Mapper.FromClaims(claims)!;

        // Then — the caller is still identified, but carries no permission claim, mirroring how a
        // malformed scope claim is dropped rather than rejecting the whole token
        Assert.Equal(id, restored.Id);
        Assert.Empty(restored.ScopePermissionClaims);
    }
}
