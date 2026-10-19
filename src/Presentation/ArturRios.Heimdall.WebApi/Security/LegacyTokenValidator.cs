using ArturRios.Jwt;
using ArturRios.Util.WebApi.Security.Authentication;
using ArturRios.Util.WebApi.Security.Configuration;
using ArturRios.Util.WebApi.Security.Interfaces;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TokenValidationResult = ArturRios.Util.WebApi.Security.Records.TokenValidationResult;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     The one place an HS256 token is accepted: during the overlap after the switch to ES256, a token
///     signed with the legacy secret (<see cref="TokenSigningSecrets" />), or the previous one, that
///     Heimdall issued before this process started (spec decision 5).
/// </summary>
/// <remarks>
///     <para>
///         <b>Enabled</b> only while a legacy secret is configured. Without one, every read path
///         refuses everything, so removing <c>HEIMDALL_AUTH_TOKEN_SECRET</c> ends the overlap.
///     </para>
///     <para>
///         <b>Only what Heimdall issued earlier.</b> This process signs ES256 alone, so a genuine
///         HS256 token was issued before it started. The secret is not Heimdall's alone, though, so
///         every read path first holds a token to what such a token looks like, and refuses it
///         otherwise:
///     </para>
///     <list type="bullet">
///         <item>
///             it carries no <c>kid</c> header. Heimdall never wrote one to an HS256 token, and a
///             <c>kid</c> naming a secret's id would make <see cref="JwtHandler" /> check that secret
///             alone, which a forgery could aim at;
///         </item>
///         <item>it carries an <c>iat</c>, and neither its <c>iat</c> nor its <c>nbf</c> is after the start;</item>
///         <item>
///             it carries an <c>exp</c> no more than its kind's longest lifetime after the start,
///             which the reader passes in. A token issued before the start expires by then.
///         </item>
///     </list>
///     <para>
///         The claims are read before the signature is checked, so they only ever narrow what is
///         accepted; the signature check that follows is what proves them. A restart moves the start
///         forward, so the bound holds per process: across restarts a token minted between two of
///         them could pass, which is bounded by one lifetime.
///     </para>
///     <para>
///         <b>The paths.</b> The bearer reader uses <see cref="ValidateBearerAsync" />, a
///         <see cref="JwtTokenValidator" /> over an HS256 configuration holding the secrets alone, so
///         an ES256 key never verifies an HS256 token here, nor a secret an ES256 one. The challenge
///         reader uses <see cref="IsSignedAndLiveAsync" />, and the application reader validates
///         under <see cref="SecurityKeysFor" />'s keys with HS256 alone. None checks the issuer or the
///         audience: each reader does that afterwards (<see cref="TokenOrigin" />), and records an
///         acceptance with <see cref="Accepted" /> only when the token passes it.
///     </para>
/// </remarks>
/// <param name="secrets">The legacy keys, from <see cref="TokenSigningSecrets.Parse" />; empty when the overlap is over.</param>
/// <param name="configuration">
///     Heimdall's token configuration; the bearer path takes it with the algorithm, keys and secret
///     replaced by the legacy ones.
/// </param>
/// <param name="jwtHandler">Checks the signature and lifetime.</param>
/// <param name="mapper">Interprets a bearer token's claims as the person.</param>
/// <param name="options">How the bearer path resolves the person once the signature is valid.</param>
/// <param name="logger">Receives one line per acceptance a reader records.</param>
/// <param name="processStart">
///     When this process started, the moment from which Heimdall issued no HS256 token. It is
///     recorded once, at start-up.
/// </param>
public sealed class LegacyTokenValidator(
    IReadOnlyList<JwtKey> secrets,
    JwtConfiguration configuration,
    JwtHandler jwtHandler,
    IAuthenticatedUserMapper mapper,
    AuthenticationOptions options,
    ILogger<LegacyTokenValidator> logger,
    DateTimeOffset processStart)
{
    /// <summary>The bearer reader's name in <see cref="AcceptedTemplate" />: <see cref="PersonTokenValidator" />.</summary>
    public const string BearerReader = "bearer";

    /// <summary>The challenge reader's name in <see cref="AcceptedTemplate" />: <see cref="JwtTwoFactorChallengeTokenIssuer" />.</summary>
    public const string ChallengeReader = "challenge";

    /// <summary>The application reader's name in <see cref="AcceptedTemplate" />: <see cref="JwtApplicationTokenService" />.</summary>
    public const string ApplicationReader = "application";

    /// <summary>The line written for each legacy token a reader accepts. It names no identity.</summary>
    public const string AcceptedTemplate =
        "Verified a legacy HS256 {Reader} token; HS256 tokens are accepted until {Variable} is removed";

    private readonly IReadOnlyList<SecurityKey> _securityKeys = [.. secrets.Select(secret => secret.ToSecurityKey())];

    private readonly JwtTokenValidator? _bearer = secrets.Count > 0
        ? new JwtTokenValidator(
            configuration with
            {
                Algorithm = JwtAlgorithms.HS256,
                Keys = secrets,
                SigningKeyId = null,
                Secret = secrets[0].Secret
            },
            jwtHandler,
            mapper,
            options)
        : null;

    /// <summary>Whether the overlap is on: a legacy secret is configured.</summary>
    public bool Enabled { get; } = secrets.Count > 0;

    /// <summary>
    ///     The legacy secrets' keys, for a reader's HS256 pass, when <paramref name="token" /> could be
    ///     one Heimdall issued before this process started; otherwise none, and always none when the
    ///     overlap is over. The reader still checks the signature, under HS256 alone.
    /// </summary>
    /// <param name="token">The token to be validated.</param>
    /// <param name="maximumLifetime">The longest lifetime a token of the reader's kind is issued with.</param>
    public IReadOnlyList<SecurityKey> SecurityKeysFor(string token, TimeSpan maximumLifetime) =>
        CouldBeIssuedEarlier(token, maximumLifetime) ? _securityKeys : [];

    /// <summary>
    ///     The bearer path: the person a legacy token names, when it could be one Heimdall issued
    ///     before this process started, is signed with a legacy secret, and is live. Any other token,
    ///     and every token when the overlap is over, gets the error a bad signature gets.
    /// </summary>
    /// <param name="token">The bearer token.</param>
    /// <param name="context">The request, which a lookup by id resolves its provider from.</param>
    /// <param name="maximumLifetime">The longest lifetime a person token is issued with.</param>
    public async Task<TokenValidationResult> ValidateBearerAsync(
        string token, HttpContext context, TimeSpan maximumLifetime)
    {
        if (_bearer is null || !CouldBeIssuedEarlier(token, maximumLifetime))
        {
            return new TokenValidationResult(null, PersonTokenValidator.InvalidToken);
        }

        return await _bearer.ValidateAsync(token, context);
    }

    /// <summary>
    ///     Whether a legacy token could be one Heimdall issued before this process started, is signed
    ///     with a legacy secret under HS256, and is live. Always <c>false</c> when the overlap is over.
    /// </summary>
    /// <param name="token">The token to be validated.</param>
    /// <param name="maximumLifetime">The longest lifetime a token of the reader's kind is issued with.</param>
    public async Task<bool> IsSignedAndLiveAsync(string token, TimeSpan maximumLifetime) =>
        CouldBeIssuedEarlier(token, maximumLifetime) && await jwtHandler.IsTokenValidAsync(token, secrets);

    /// <summary>
    ///     Records that <paramref name="reader" /> accepted a legacy token: one Information line, from
    ///     <see cref="AcceptedTemplate" />, naming the reader and the variable whose removal ends the
    ///     overlap.
    /// </summary>
    /// <param name="reader"><see cref="BearerReader" />, <see cref="ChallengeReader" /> or <see cref="ApplicationReader" />.</param>
    public void Accepted(string reader) =>
        logger.LogInformation(AcceptedTemplate, reader, TokenSigningSecrets.SecretVariable);

    /// <summary>
    ///     Whether the token looks like one Heimdall issued before this process started: no
    ///     <c>kid</c>; an <c>iat</c>, and no <c>iat</c> or <c>nbf</c> after the start; an <c>exp</c>
    ///     within <paramref name="maximumLifetime" /> of the start. Unverified, so it only narrows.
    /// </summary>
    private bool CouldBeIssuedEarlier(string token, TimeSpan maximumLifetime)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        JsonWebToken read;

        try
        {
            read = new JsonWebToken(token);
        }
        catch (Exception exception) when (exception is ArgumentException or SecurityTokenException)
        {
            return false;
        }

        if (read.TryGetHeaderValue<object>(JwtHeaderParameterNames.Kid, out _))
        {
            return false;
        }

        if (!TryGetInstant(read, JwtRegisteredClaimNames.Iat, out var issuedAt) || issuedAt > processStart)
        {
            return false;
        }

        // An nbf is optional, but one that is present must be readable and not after the start.
        if (read.TryGetPayloadValue<object>(JwtRegisteredClaimNames.Nbf, out _) &&
            (!TryGetInstant(read, JwtRegisteredClaimNames.Nbf, out var notBefore) || notBefore > processStart))
        {
            return false;
        }

        return TryGetInstant(read, JwtRegisteredClaimNames.Exp, out var expires) &&
               expires <= processStart + maximumLifetime;
    }

    /// <summary>A NumericDate claim as an instant; <c>false</c> when it is absent or not a whole number of seconds.</summary>
    private static bool TryGetInstant(JsonWebToken token, string claim, out DateTimeOffset instant)
    {
        instant = default;

        if (!token.TryGetPayloadValue<long>(claim, out var seconds) ||
            seconds < DateTimeOffset.MinValue.ToUnixTimeSeconds() ||
            seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return false;
        }

        instant = DateTimeOffset.FromUnixTimeSeconds(seconds);

        return true;
    }
}
