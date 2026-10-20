using ArturRios.Jwt;
using ArturRios.Util.WebApi.Security.Authentication;
using ArturRios.Util.WebApi.Security.Interfaces;
using ArturRios.Util.WebApi.Security.Records;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     The bearer-token validator <c>AuthenticationMiddleware</c> runs: the library's
///     <see cref="JwtTokenValidator" />, which checks the ES256 signature and lifetime and maps the
///     claims, followed by the issuer and audience check it does not make (<see cref="TokenOrigin" />).
/// </summary>
/// <remarks>
///     <para>
///         A token passes only under the configured issuer and one of the three person audiences
///         (<see cref="PersonTokenAudiences" />). The challenge audience is among them so that
///         <see cref="MfaPendingGuardFilter" /> still answers a challenge used as a bearer token with
///         its own refusal; the application audience is not, since an application token is never a
///         person (TH-35).
///     </para>
///     <para>
///         An HS256 token is refused like any other bad signature: the configuration pins ES256 and
///         holds EC keys alone, and the shared secret Heimdall once signed with is no longer read
///         (Threat Model TH-43).
///     </para>
///     <para>
///         A token refused here gets the answer a bad signature gets, so a correctly signed token for
///         another service cannot be told apart from a forged one.
///     </para>
/// </remarks>
public sealed class PersonTokenValidator(
    JwtTokenValidator inner,
    JwtConfiguration configuration,
    PersonTokenAudiences audiences) : ITokenValidator
{
    /// <summary>The error <see cref="JwtTokenValidator" /> gives a token whose signature or lifetime fails.</summary>
    public const string InvalidToken = "Invalid token";

    /// <inheritdoc />
    public async Task<TokenValidationResult> ValidateAsync(string token, HttpContext context)
    {
        var result = await inner.ValidateAsync(token, context);

        if (result.User is null)
        {
            return result;
        }

        return TokenOrigin.IsIssuedFor(
            token, configuration.Issuer, audiences.Person, audiences.TwoFactorChallenge, audiences.SubjectRights)
            ? result
            : new TokenValidationResult(null, InvalidToken);
    }
}
