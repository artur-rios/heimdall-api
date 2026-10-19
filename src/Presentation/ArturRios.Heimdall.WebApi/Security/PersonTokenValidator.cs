using ArturRios.Jwt;
using ArturRios.Util.WebApi.Security.Authentication;
using ArturRios.Util.WebApi.Security.Interfaces;
using ArturRios.Util.WebApi.Security.Records;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     The bearer-token validator <c>AuthenticationMiddleware</c> runs: the library's
///     <see cref="JwtTokenValidator" />, which checks the ES256 signature and lifetime and maps the
///     claims, then, during the overlap, <see cref="LegacyTokenValidator" /> for an HS256 token Heimdall
///     issued before it started, followed by the issuer and audience check neither makes
///     (<see cref="TokenOrigin" />).
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
///         <b>The overlap.</b> A token the ES256 check refuses is offered to the legacy path only while
///         a legacy secret is configured, bounded by a person token's lifetime, which a
///         subject-rights-only token shares. An acceptance by that path is logged once it has passed
///         the origin check too, so the log counts only tokens Heimdall actually took.
///     </para>
///     <para>
///         A token refused here gets the answer a bad signature gets, so a correctly signed token for
///         another service cannot be told apart from a forged one.
///     </para>
/// </remarks>
public sealed class PersonTokenValidator(
    JwtTokenValidator inner,
    LegacyTokenValidator legacy,
    JwtConfiguration configuration,
    PersonTokenAudiences audiences) : ITokenValidator
{
    /// <summary>The error <see cref="JwtTokenValidator" /> gives a token whose signature or lifetime fails.</summary>
    public const string InvalidToken = "Invalid token";

    /// <inheritdoc />
    public async Task<TokenValidationResult> ValidateAsync(string token, HttpContext context)
    {
        var result = await inner.ValidateAsync(token, context);
        var byLegacy = false;

        if (result.User is null && legacy.Enabled)
        {
            var legacyResult = await legacy.ValidateBearerAsync(
                token, context, TimeSpan.FromSeconds(configuration.ExpirationInSeconds));

            // Otherwise the ES256 refusal stands: an HS256 token always fails that check, so its
            // answer says nothing a forger could use.
            if (legacyResult.User is not null)
            {
                result = legacyResult;
                byLegacy = true;
            }
        }

        if (result.User is null)
        {
            return result;
        }

        if (!TokenOrigin.IsIssuedFor(
                token, configuration.Issuer, audiences.Person, audiences.TwoFactorChallenge, audiences.SubjectRights))
        {
            return new TokenValidationResult(null, InvalidToken);
        }

        if (byLegacy)
        {
            legacy.Accepted(LegacyTokenValidator.BearerReader);
        }

        return result;
    }
}
