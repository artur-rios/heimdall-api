using System.Text;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for GoogleIdTokenClaims (FR-GO-19). The real GoogleIdTokenVerifier cannot be tested
// directly — it calls the static, network-bound GoogleJsonWebSignature.ValidateAsync, and reaching
// it would mean contacting Google or forging Google's signature — so the claim-reading half was
// split out and is pinned here instead. What these tests protect is narrow and specific: the
// distinction between a token that ASSERTS "not verified" and one that asserts nothing at all.
// Collapsing the two would let a client holding a token without the `email` scope downgrade a
// stored EmailVerified = true, which is exactly what FR-GO-19 forbids.
//
// The four claim shapes are pinned deliberately, not defensively. TryGetPayloadValue<bool> reporting
// an explicit JSON null as present-and-false is measured behaviour of
// Microsoft.IdentityModel.JsonWebTokens 8.19.2 rather than a documented contract, and the bool?
// overload is what tells that case apart. A package bump that changes either should fail here rather
// than quietly downgrade someone's verified address in production.
public class GoogleIdTokenClaimsTests
{
    /// <summary>
    ///     Builds an unsigned token whose payload is exactly <paramref name="payloadJson" />. These
    ///     tests read claims off a token the caller has already validated, so the signature is
    ///     irrelevant here — only the payload segment's shape matters, and writing it as raw JSON is
    ///     the only way to express a claim that is present but JSON <c>null</c>.
    /// </summary>
    private static string TokenWithPayload(string payloadJson) =>
        $"{Base64Url("""{"alg":"RS256","typ":"JWT"}""")}.{Base64Url(payloadJson)}.not-a-real-signature";

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    [UnitFact]
    public void GivenClaimAssertsTrue_WhenReadingEmailVerified_ThenReturnsTrue()
    {
        // Given the ordinary token of a verified Google account
        var token = TokenWithPayload("""{"sub":"1","email":"a@b.test","email_verified":true}""");

        // When / Then
        Assert.True(GoogleIdTokenClaims.EmailVerified(token));
    }

    [UnitFact]
    public void GivenClaimAssertsFalse_WhenReadingEmailVerified_ThenReturnsFalse()
    {
        // Given Google actively asserting the address is not verified — distinct from saying nothing
        var token = TokenWithPayload("""{"sub":"1","email":"a@b.test","email_verified":false}""");

        // When / Then
        Assert.False(GoogleIdTokenClaims.EmailVerified(token));
    }

    [UnitFact]
    public void GivenClaimIsAbsent_WhenReadingEmailVerified_ThenReturnsNull()
    {
        // Given a token obtained without the `email` scope: it carries no such claim, and must not
        // be read as an assertion of false (FR-GO-19)
        var token = TokenWithPayload("""{"sub":"1","email":"a@b.test"}""");

        // When / Then
        Assert.Null(GoogleIdTokenClaims.EmailVerified(token));
    }

    [UnitFact]
    public void GivenClaimIsExplicitlyNull_WhenReadingEmailVerified_ThenReturnsNull()
    {
        // Given the shape TryGetPayloadValue<bool> alone gets wrong — it reports this as
        // present-and-false, which would downgrade a stored true
        var token = TokenWithPayload("""{"sub":"1","email":"a@b.test","email_verified":null}""");

        // When / Then
        Assert.Null(GoogleIdTokenClaims.EmailVerified(token));
    }

    [UnitFact]
    public void GivenTokenIsMalformed_WhenReadingEmailVerified_ThenReturnsNullRatherThanThrowing()
    {
        // Given something that is not a token at all. The caller's catch handles only
        // InvalidJwtException, so anything thrown here would turn a sign-in Google vouched for into
        // a 500; "asserts nothing" is the safe answer, and it writes nothing.
        Assert.Null(GoogleIdTokenClaims.EmailVerified("not-a-token"));
        Assert.Null(GoogleIdTokenClaims.EmailVerified(string.Empty));
        Assert.Null(GoogleIdTokenClaims.EmailVerified("only.two-segments"));
    }
}
