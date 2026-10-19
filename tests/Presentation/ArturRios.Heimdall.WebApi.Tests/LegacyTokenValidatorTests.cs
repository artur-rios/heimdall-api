using System.Security.Cryptography;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Jwt;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.WebApi.Security.Configuration;
using ArturRios.Util.WebApi.Security.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for LegacyTokenValidator (spec decision 5, rulings R1 and R4): during the overlap, an
// HS256 token signed with the legacy secret, or the previous one, is accepted by each of its three
// read paths only when Heimdall could have issued it before this process started. It must carry no
// kid, carry an iat, be issued and valid from before the start, and expire within one lifetime of
// its kind after it. With no secret nothing is accepted. Acceptances are logged only when a reader
// records one, one line each, naming no identity.
public class LegacyTokenValidatorTests
{
    private const string Issuer = "heimdall-tests";
    private const string Audience = "heimdall-tests";

    // Generated for these tests (openssl rand -hex 20); no environment signs with them.
    private const string Secret = "unit-legacy-current-f0af7b52bf6662143d29ce69ba84b4d267bf9cc1";
    private const string PreviousSecret = "unit-legacy-previous-809822953d3e42ae62bb231ea1ec353960e38ce9";
    private const string OtherSecret = "unit-legacy-unknown-0fc17a3efeaba5913219e85c97535738264fef67";

    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private static readonly IdentityUserMapper Mapper = new(new PersonTokenAudiences(Audience));

    private static readonly AuthenticationOptions Options =
        new() { EnableJwt = true, JwtMode = JwtValidationMode.ClaimsOnly };

    // The process started half an hour ago, so a test token can be issued, or become valid, after the
    // start and still be live now.
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow.AddMinutes(-30);

    private readonly IdentityUser _user = new(Guid.NewGuid(), (int)Roles.SystemAdmin);

    // The ES256 configuration Heimdall signs with, as the readers will hand it over.
    private static JwtConfiguration Configuration() =>
        TokenSigningKeys.Parse(UnitSigningKeys.Create().EnvValue, null)
            .ApplyTo(new JwtConfiguration(3600, Issuer, Audience, string.Empty, []));

    private LegacyTokenValidator Validator(
        IReadOnlyList<JwtKey> secrets,
        RecordingLogger<LegacyTokenValidator>? logger = null,
        DateTimeOffset? processStart = null) =>
        new(secrets, Configuration(), new JwtHandler(), Mapper, Options,
            logger ?? new RecordingLogger<LegacyTokenValidator>(), processStart ?? _start);

    private Dictionary<string, object> Claims() =>
        Mapper.ToClaims(_user).ToDictionary(claim => claim.Key, object (claim) => claim.Value);

    // A token as the 2.x process issued it: no kid, issued and valid from ten minutes before this
    // process started, and live for one lifetime from then.
    private string Legacy(
        SigningCredentials? credentials = null,
        TimeSpan? issuedAt = null,
        TimeSpan? notBefore = null,
        TimeSpan? expires = null,
        bool withIssuedAt = true) =>
        UnitSigningKeys.TokenAt(
            credentials ?? UnitSigningKeys.Hs256(Secret),
            Claims(),
            Issuer,
            Audience,
            withIssuedAt ? At(issuedAt ?? TimeSpan.FromMinutes(-10)) : null,
            At(notBefore ?? TimeSpan.FromMinutes(-10)),
            At(expires ?? TimeSpan.FromMinutes(50)));

    // An instant relative to the process start.
    private DateTime At(TimeSpan sinceStart) => _start.Add(sinceStart).UtcDateTime;

    // Whether each read path accepts the token: the bearer reader's, the challenge reader's, and the
    // application reader's HS256 pass, which validates under SecurityKeysFor's keys with HS256 alone.
    private static async Task<(bool Bearer, bool SignedAndLive, bool UnderOfferedKeys)> ReadByEveryPath(
        LegacyTokenValidator validator, string token, TimeSpan lifetime)
    {
        var bearer = await validator.ValidateBearerAsync(token, new DefaultHttpContext(), lifetime);
        var signedAndLive = await validator.IsSignedAndLiveAsync(token, lifetime);
        var keys = validator.SecurityKeysFor(token, lifetime);

        var underOfferedKeys = keys.Count > 0 && (await new JsonWebTokenHandler().ValidateTokenAsync(token,
            new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                IssuerSigningKeys = keys,
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = TimeSpan.Zero
            })).IsValid;

        return (bearer.User is not null, signedAndLive, underOfferedKeys);
    }

    [UnitFact]
    public async Task GivenNoSecrets_WhenAnHs256TokenArrives_ThenNothingIsAccepted()
    {
        // Given
        var validator = Validator(TokenSigningSecrets.Parse(null, null));
        var token = Legacy();

        // When
        var result = await validator.ValidateBearerAsync(token, new DefaultHttpContext(), Lifetime);

        // Then
        Assert.False(validator.Enabled);
        Assert.Empty(validator.SecurityKeysFor(token, Lifetime));
        Assert.Null(result.User);
        Assert.Equal("Invalid token", result.Error);
        Assert.False(await validator.IsSignedAndLiveAsync(token, Lifetime));
    }

    [UnitFact]
    public async Task GivenTheSecret_WhenAnHs256TokenItSignedArrives_ThenItsUserIsMapped()
    {
        // Given
        var validator = Validator(TokenSigningSecrets.Parse(Secret, null));

        // When
        var result = await validator.ValidateBearerAsync(Legacy(), new DefaultHttpContext(), Lifetime);

        // Then
        Assert.True(validator.Enabled);
        var user = Assert.IsType<IdentityUser>(result.User);
        Assert.Equal(_user.Id, user.Id);
        Assert.Null(result.Error);
    }

    [UnitFact]
    public async Task GivenTheSecret_WhenATokenIssuedBeforeTheProcessStartedArrives_ThenEveryPathAcceptsIt()
    {
        // Given
        var validator = Validator(TokenSigningSecrets.Parse(Secret, null));

        // When
        var outcome = await ReadByEveryPath(validator, Legacy(), Lifetime);

        // Then
        Assert.Equal((true, true, true), outcome);
    }

    [UnitFact]
    public async Task GivenThePreviousSecret_WhenAnHs256TokenItSignedArrives_ThenItIsAccepted()
    {
        // Given
        var validator = Validator(TokenSigningSecrets.Parse(Secret, PreviousSecret));

        // When
        var outcome = await ReadByEveryPath(validator, Legacy(UnitSigningKeys.Hs256(PreviousSecret)), Lifetime);

        // Then
        Assert.Equal((true, true, true), outcome);
    }

    [UnitFact]
    public void GivenTheOverlap_WhenAKidlessTokenAsksForKeys_ThenEveryLegacySecretIsOffered()
    {
        // Given
        var validator = Validator(TokenSigningSecrets.Parse(Secret, PreviousSecret));

        // When
        var keys = validator.SecurityKeysFor(Legacy(), Lifetime);

        // Then
        Assert.All(keys, key => Assert.IsType<SymmetricSecurityKey>(key));
        Assert.Equal(
            [TokenSigningSecrets.CurrentKeyId, TokenSigningSecrets.PreviousKeyId],
            keys.Select(key => key.KeyId));
    }

    [UnitFact]
    public async Task GivenTheSecret_WhenAnHs256TokenSignedWithAnotherSecretArrives_ThenItIsRefused()
    {
        // Given
        var validator = Validator(TokenSigningSecrets.Parse(Secret, PreviousSecret));

        // When
        var outcome = await ReadByEveryPath(validator, Legacy(UnitSigningKeys.Hs256(OtherSecret)), Lifetime);

        // Then
        Assert.Equal((false, false, false), outcome);
    }

    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GivenTheSecret_WhenAnEs256TokenArrives_ThenItIsRefused(bool withKid)
    {
        // Given — without a kid, only the algorithm tells it apart from a legacy token
        var validator = Validator(TokenSigningSecrets.Parse(Secret, null));
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credentials = withKid
            ? UnitSigningKeys.Es256(UnitSigningKeys.Create().Key)
            : new SigningCredentials(new ECDsaSecurityKey(ecdsa), SecurityAlgorithms.EcdsaSha256);

        // When
        var outcome = await ReadByEveryPath(validator, Legacy(credentials), Lifetime);

        // Then
        Assert.Equal((false, false, false), outcome);
    }

    [UnitFact]
    public async Task GivenAnExpiredHs256Token_WhenItArrives_ThenItIsRefused()
    {
        // Given — the process started now, so the token was issued before it and only its expiry refuses it
        var validator = Validator(TokenSigningSecrets.Parse(Secret, null), processStart: DateTimeOffset.UtcNow);
        var token = UnitSigningKeys.ExpiredToken(UnitSigningKeys.Hs256(Secret), Claims(), Issuer, Audience);

        // When
        var outcome = await ReadByEveryPath(validator, token, Lifetime);

        // Then
        Assert.Equal((false, false, false), outcome);
    }

    [UnitTheory]
    [InlineData(TokenSigningSecrets.CurrentKeyId)]
    [InlineData(TokenSigningSecrets.PreviousKeyId)]
    [InlineData("a-key-id-nothing-here-knows")]
    public async Task GivenAnHs256TokenCarryingAKid_WhenItArrives_ThenNoPathAcceptsItAndNoKeyIsOffered(string kid)
    {
        // Given — signed with the very secret the kid names; Heimdall's HS256 tokens never carried one
        var validator = Validator(TokenSigningSecrets.Parse(Secret, PreviousSecret));
        var secret = kid == TokenSigningSecrets.PreviousKeyId ? PreviousSecret : Secret;
        var token = Legacy(UnitSigningKeys.Hs256(secret, kid));

        // When
        var bearer = await validator.ValidateBearerAsync(token, new DefaultHttpContext(), Lifetime);

        // Then
        Assert.Empty(validator.SecurityKeysFor(token, Lifetime));
        Assert.Null(bearer.User);
        Assert.Equal("Invalid token", bearer.Error);
        Assert.False(await validator.IsSignedAndLiveAsync(token, Lifetime));
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("a.b.c")]
    public async Task GivenAnUnreadableToken_WhenItArrives_ThenNoPathAcceptsIt(string token)
    {
        // Given
        var validator = Validator(TokenSigningSecrets.Parse(Secret, null));

        // When
        var outcome = await ReadByEveryPath(validator, token, Lifetime);

        // Then
        Assert.Equal((false, false, false), outcome);
    }

    [UnitTheory]
    [InlineData("iat")]
    [InlineData("nbf")]
    public async Task GivenATokenIssuedAfterTheProcessStarted_WhenItArrives_ThenEveryPathRefusesIt(string claim)
    {
        // Given — a minute after the start, which this process, signing ES256 only, could not have done
        var validator = Validator(TokenSigningSecrets.Parse(Secret, null));
        var afterStart = TimeSpan.FromMinutes(1);
        var token = claim == "iat" ? Legacy(issuedAt: afterStart) : Legacy(notBefore: afterStart);

        // When
        var outcome = await ReadByEveryPath(validator, token, Lifetime);

        // Then
        Assert.Equal((false, false, false), outcome);
    }

    [UnitFact]
    public async Task GivenATokenExpiringBeyondOneLifetimeAfterTheStart_WhenItArrives_ThenOnlyALongerLifetimeAcceptsIt()
    {
        // Given — five minutes past one lifetime after the start
        var validator = Validator(TokenSigningSecrets.Parse(Secret, null));
        var token = Legacy(expires: Lifetime + TimeSpan.FromMinutes(5));

        // When
        var underItsLifetime = await ReadByEveryPath(validator, token, Lifetime);
        var underALongerOne = await ReadByEveryPath(validator, token, Lifetime * 2);

        // Then
        Assert.Equal((false, false, false), underItsLifetime);
        Assert.Equal((true, true, true), underALongerOne);
    }

    [UnitFact]
    public async Task GivenATokenWithoutAnIssuedAt_WhenItArrives_ThenEveryPathRefusesIt()
    {
        // Given — every token Heimdall issued carries an iat
        var validator = Validator(TokenSigningSecrets.Parse(Secret, null));

        // When
        var outcome = await ReadByEveryPath(validator, Legacy(withIssuedAt: false), Lifetime);

        // Then
        Assert.Equal((false, false, false), outcome);
    }

    [UnitTheory]
    [InlineData(LegacyTokenValidator.BearerReader,
        "Verified a legacy HS256 bearer token; HS256 tokens are accepted until HEIMDALL_AUTH_TOKEN_SECRET is removed")]
    [InlineData(LegacyTokenValidator.ChallengeReader,
        "Verified a legacy HS256 challenge token; HS256 tokens are accepted until HEIMDALL_AUTH_TOKEN_SECRET is removed")]
    [InlineData(LegacyTokenValidator.ApplicationReader,
        "Verified a legacy HS256 application token; HS256 tokens are accepted until HEIMDALL_AUTH_TOKEN_SECRET is removed")]
    public void GivenAnAcceptance_WhenRecorded_ThenOneInformationLineNamesTheReaderAndTheVariable(
        string reader, string expected)
    {
        // Given
        var logger = new RecordingLogger<LegacyTokenValidator>();
        var validator = Validator(TokenSigningSecrets.Parse(Secret, null), logger);

        // When
        validator.Accepted(reader);

        // Then
        Assert.Equal([(LogLevel.Information, expected)], logger.Entries);
    }

    [UnitFact]
    public void GivenTwoAcceptances_WhenRecorded_ThenEachWritesItsOwnLine()
    {
        // Given
        var logger = new RecordingLogger<LegacyTokenValidator>();
        var validator = Validator(TokenSigningSecrets.Parse(Secret, null), logger);

        // When
        validator.Accepted(LegacyTokenValidator.BearerReader);
        validator.Accepted(LegacyTokenValidator.ApplicationReader);

        // Then
        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
    }

    [UnitFact]
    public async Task GivenTokensReadByEveryPath_WhenNoAcceptanceIsRecorded_ThenNothingIsLogged()
    {
        // Given — one token accepted and one refused; recording an acceptance is the reader's call
        var logger = new RecordingLogger<LegacyTokenValidator>();
        var validator = Validator(TokenSigningSecrets.Parse(Secret, null), logger);

        // When
        await ReadByEveryPath(validator, Legacy(), Lifetime);
        await ReadByEveryPath(validator, Legacy(UnitSigningKeys.Hs256(OtherSecret)), Lifetime);

        // Then
        Assert.Empty(logger.Entries);
    }
}
