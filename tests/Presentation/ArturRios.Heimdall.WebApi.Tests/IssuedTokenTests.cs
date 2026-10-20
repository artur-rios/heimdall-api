using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Jwt;
using ArturRios.Util.Test.Attributes;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for the tokens Heimdall issues (spec, "Tokens"): every kind — the person token, the
// subject-rights-only token, the UC-38 challenge token and the UC-56 application token — is signed
// ES256 with the current key and names it by its thumbprint in the kid header, while its issuer and
// audience stay what they were. A consumer holding only the published key set verifies each one.
public class IssuedTokenTests
{
    private const string Issuer = "heimdall-tests";

    [UnitTheory]
    [InlineData("person")]
    [InlineData("subject-rights")]
    [InlineData("challenge")]
    [InlineData("application")]
    public async Task GivenTheSigningKeys_WhenEachTokenKindIsIssued_ThenItIsEs256UnderTheCurrentKidWithItsUsualOrigin(
        string kind)
    {
        // Given — the configuration start-up builds, keys.ApplyTo over the token settings, for a
        // fresh key and a previous one, which is published but must never sign
        var keys = TokenSigningKeys.Parse(UnitSigningKeys.Create().EnvValue, UnitSigningKeys.Create().EnvValue);
        var readers = new UnitTokenReaders(keys, Issuer, "heimdall-tests");

        var (issue, audience) = kind switch
        {
            "person" => (() => readers.AuthIssuer.IssueAsync(
                    new AuthTokenSubject(Guid.NewGuid(), "Ada", (int)Roles.SystemAdmin, null, [], Guid.NewGuid())),
                readers.Audiences.Person),
            "subject-rights" => (() => readers.AuthIssuer.IssueSubjectRightsOnlyAsync(
                    Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid()),
                readers.Audiences.SubjectRights),
            "challenge" => (() => readers.Challenge.IssueAsync(
                    Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid(), Guid.NewGuid()),
                readers.Audiences.TwoFactorChallenge),
            "application" => (new Func<Task<AuthToken>>(() => readers.Application.IssueAsync(
                    new ApplicationTokenSubject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Billing"))),
                readers.ApplicationOptions.Audience),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        // When
        var token = (await issue()).Token;

        // Then — only the header changed: ES256, the current key's thumbprint, the usual issuer and
        // audience, and a signature the published key set verifies
        var read = new JsonWebToken(token);
        Assert.Equal("ES256", read.Alg);
        Assert.Equal(keys.Current.Id, read.Kid);
        Assert.NotEqual(keys.Previous!.Id, read.Kid);
        Assert.Equal("JWT", read.Typ);
        Assert.Equal(Issuer, read.Issuer);
        Assert.Equal(audience, Assert.Single(read.Audiences));
        Assert.True(await new JwtHandler().IsTokenValidAsync(token, JwksDocument.Parse(keys.JwksJson)));
    }
}
