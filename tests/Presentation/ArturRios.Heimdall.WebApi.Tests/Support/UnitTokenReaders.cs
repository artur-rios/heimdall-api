using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Jwt;
using ArturRios.Output;
using ArturRios.Util.WebApi.Security.Authentication;
using ArturRios.Util.WebApi.Security.Configuration;
using ArturRios.Util.WebApi.Security.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     Heimdall's issuers and its three token readers, wired as <c>Startup</c> wires them: over one
///     ES256 configuration built through <see cref="TokenSigningKeys.ApplyTo" />, and one
///     <see cref="LegacyTokenValidator" /> holding the legacy secrets given (none: the overlap is over)
///     and a known process start.
/// </summary>
/// <remarks>
///     The process started two minutes ago, in whole seconds as a token's NumericDate claims are, so a
///     test can mint a legacy token issued before it, or after it, that is still live now, even under
///     the challenge token's ten-minute lifetime. <see cref="TokenFor" /> mints, for each reader, a
///     token right in every respect but the key it is signed with.
/// </remarks>
public sealed class UnitTokenReaders
{
    /// <summary>The issuer every token is issued under unless a test names another.</summary>
    public const string DefaultIssuer = "heimdall-tests";

    /// <summary>The person audience unless a test names another.</summary>
    public const string DefaultPersonAudience = "heimdall-tests";

    /// <summary>The application audience.</summary>
    public const string ApplicationAudience = "heimdall-tests-applications";

    /// <summary>A person token's lifetime: <c>HEIMDALL_AUTH_TOKEN_EXPIRATION_IN_SECONDS</c>'s default.</summary>
    public static readonly TimeSpan PersonLifetime = TimeSpan.FromHours(1);

    /// <summary>
    ///     An application token's lifetime: twenty minutes, distinct from both other kinds', so a test
    ///     can tell which lifetime a reader bounds a legacy token by.
    /// </summary>
    public static readonly TimeSpan ApplicationLifetime = TimeSpan.FromMinutes(20);

    /// <summary>The person every person-shaped token names.</summary>
    public readonly IdentityUser Person = new(Guid.NewGuid(), (int)Roles.SystemAdmin) { SecurityStamp = Guid.NewGuid() };

    /// <summary>The challenge every challenge-shaped token names.</summary>
    public readonly Guid ChallengeId = Guid.NewGuid();

    /// <summary>The application every application-shaped token names.</summary>
    public readonly ApplicationTokenSubject Subject = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Billing");

    /// <param name="legacySecrets">The legacy keys, from <see cref="TokenSigningSecrets.Parse" />.</param>
    /// <param name="keys">The signing keys; omitted, a fresh key and no previous one.</param>
    /// <param name="issuer">The configured issuer.</param>
    /// <param name="personAudience">The configured person audience.</param>
    public UnitTokenReaders(
        IReadOnlyList<JwtKey> legacySecrets,
        TokenSigningKeys? keys = null,
        string issuer = DefaultIssuer,
        string personAudience = DefaultPersonAudience)
    {
        Keys = keys ?? NewKeys();
        Configuration = Keys.ApplyTo(
            new JwtConfiguration(PersonLifetime.TotalSeconds, issuer, personAudience, string.Empty, []));
        Audiences = PersonTokenAudiences.From(Configuration);
        Mapper = new IdentityUserMapper(Audiences);
        ApplicationOptions = new ApplicationTokenOptions(ApplicationAudience, ApplicationLifetime);

        var handler = new JwtHandler();
        var options = new AuthenticationOptions { EnableJwt = true, JwtMode = JwtValidationMode.ClaimsOnly };

        Legacy = new LegacyTokenValidator(
            legacySecrets, Configuration, handler, Mapper, options, Log, ProcessStart);
        Bearer = new PersonTokenValidator(
            new JwtTokenValidator(Configuration, handler, Mapper, options), Legacy, Configuration, Audiences);
        Challenge = new JwtTwoFactorChallengeTokenIssuer(Configuration, Audiences, handler, Mapper, Legacy);
        Application = new JwtApplicationTokenService(Configuration, handler, ApplicationOptions, Legacy);
        AuthIssuer = new JwtAuthTokenIssuer(Configuration, Audiences, handler, Mapper, new UnreadPermissions());
    }

    /// <summary>When the process started: two minutes ago, in whole seconds.</summary>
    public DateTimeOffset ProcessStart { get; } =
        DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds());

    public TokenSigningKeys Keys { get; }

    /// <summary>The ES256 configuration every issuer and reader is given.</summary>
    public JwtConfiguration Configuration { get; }

    public PersonTokenAudiences Audiences { get; }

    public IdentityUserMapper Mapper { get; }

    public ApplicationTokenOptions ApplicationOptions { get; }

    /// <summary>What <see cref="Legacy" /> logs: one line per acceptance a reader records.</summary>
    public RecordingLogger<LegacyTokenValidator> Log { get; } = new();

    public LegacyTokenValidator Legacy { get; }

    /// <summary>The bearer reader.</summary>
    public PersonTokenValidator Bearer { get; }

    /// <summary>The challenge issuer, and the challenge reader.</summary>
    public JwtTwoFactorChallengeTokenIssuer Challenge { get; }

    /// <summary>The application-token issuer, and the application reader.</summary>
    public JwtApplicationTokenService Application { get; }

    /// <summary>The person and subject-rights issuer, for subjects that act within no scope.</summary>
    public JwtAuthTokenIssuer AuthIssuer { get; }

    /// <summary>A fresh signing key and no previous one, as start-up reads them.</summary>
    public static TokenSigningKeys NewKeys() => TokenSigningKeys.Parse(UnitSigningKeys.Create().EnvValue, null);

    /// <summary>An instant relative to <see cref="ProcessStart" />.</summary>
    public DateTime At(TimeSpan sinceStart) => ProcessStart.Add(sinceStart).UtcDateTime;

    /// <summary>
    ///     The claims and the audience a token must carry to be read by <paramref name="reader" /> (one of
    ///     <see cref="LegacyTokenValidator" />'s reader names): the person's under the person audience,
    ///     the challenge's under the challenge audience, or the application's under its own.
    /// </summary>
    public (Dictionary<string, object> Claims, string Audience) PayloadFor(string reader) => reader switch
    {
        LegacyTokenValidator.BearerReader => (ClaimsOf(Mapper.ToClaims(Person)), Audiences.Person),
        LegacyTokenValidator.ChallengeReader => (ClaimsOf(Mapper.ToClaims(
            new IdentityUser(Person.Id, Person.RoleId)
            {
                MfaPending = true, ChallengeId = ChallengeId, SecurityStamp = Person.SecurityStamp
            })), Audiences.TwoFactorChallenge),
        LegacyTokenValidator.ApplicationReader => (new Dictionary<string, object>
        {
            [ApplicationTokenClaims.TokenKind] = ApplicationTokenClaims.ApplicationKind,
            [ApplicationTokenClaims.Id] = Subject.ApplicationId.ToString(),
            [ApplicationTokenClaims.ScopeId] = Subject.ScopeId.ToString(),
            [ApplicationTokenClaims.CredentialId] = Subject.CredentialId.ToString(),
            [ApplicationTokenClaims.Name] = Subject.Name
        }, ApplicationOptions.Audience),
        _ => throw new ArgumentOutOfRangeException(nameof(reader), reader, null)
    };

    /// <summary>
    ///     A token <paramref name="reader" /> would read, signed with <paramref name="credentials" />:
    ///     its payload, under the configured issuer unless another is named, issued and valid from three
    ///     minutes before the process started, and expiring eight minutes after it, within every kind's
    ///     lifetime. Only the key, or an instant a test names, can then make a reader refuse it.
    /// </summary>
    public string TokenFor(
        string reader,
        SigningCredentials credentials,
        string? issuer = null,
        TimeSpan? issuedAt = null,
        TimeSpan? expires = null)
    {
        var (claims, audience) = PayloadFor(reader);

        return UnitSigningKeys.TokenAt(
            credentials,
            claims,
            issuer ?? Configuration.Issuer,
            audience,
            At(issuedAt ?? TimeSpan.FromMinutes(-3)),
            At(TimeSpan.FromMinutes(-3)),
            At(expires ?? TimeSpan.FromMinutes(8)));
    }

    /// <summary>
    ///     Whether <paramref name="reader" /> accepts <paramref name="token" />: the bearer reader resolves
    ///     the person, the challenge reader returns the challenge, the application reader the
    ///     application, each exactly as <see cref="PayloadFor" /> names them.
    /// </summary>
    public async Task<bool> AcceptsAsync(string reader, string token) => reader switch
    {
        LegacyTokenValidator.BearerReader =>
            (await Bearer.ValidateAsync(token, new DefaultHttpContext())).User?.Id == Person.Id,
        LegacyTokenValidator.ChallengeReader =>
            await Challenge.ValidateAsync(token) ==
            new TwoFactorChallengePrincipal(Person.Id, ChallengeId, Person.SecurityStamp),
        LegacyTokenValidator.ApplicationReader =>
            await Application.ValidateAsync(token) is { } principal &&
            (principal.ApplicationId, principal.ScopeId, principal.CredentialId) ==
            (Subject.ApplicationId, Subject.ScopeId, Subject.CredentialId),
        _ => throw new ArgumentOutOfRangeException(nameof(reader), reader, null)
    };

    /// <summary>
    ///     Whether <paramref name="reader" /> refuses <paramref name="token" /> outright: no user, no
    ///     challenge, no application.
    /// </summary>
    public async Task<bool> RefusesAsync(string reader, string token) => reader switch
    {
        LegacyTokenValidator.BearerReader => (await Bearer.ValidateAsync(token, new DefaultHttpContext())).User is null,
        LegacyTokenValidator.ChallengeReader => await Challenge.ValidateAsync(token) is null,
        LegacyTokenValidator.ApplicationReader => await Application.ValidateAsync(token) is null,
        _ => throw new ArgumentOutOfRangeException(nameof(reader), reader, null)
    };

    /// <summary>The line <see cref="LegacyTokenValidator.Accepted" /> writes for <paramref name="reader" />.</summary>
    public static string AcceptedLine(string reader) =>
        $"Verified a legacy HS256 {reader} token; HS256 tokens are accepted until " +
        $"{TokenSigningSecrets.SecretVariable} is removed";

    private static Dictionary<string, object> ClaimsOf(Dictionary<string, string> claims) =>
        claims.ToDictionary(claim => claim.Key, object (claim) => claim.Value);

    /// <summary>
    ///     The permission reader a subject acting within no scope never touches. Every member throws,
    ///     so a read would fail the test rather than pass unnoticed.
    /// </summary>
    private sealed class UnreadPermissions : IAsyncReadOnlyRepository<ScopePermission, long>
    {
        public IQueryable<ScopePermission> Query() => throw new InvalidOperationException("Not read.");

        public Task<DataOutput<IEnumerable<ScopePermission>>> GetAllAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Not read.");

        public Task<DataOutput<ScopePermission?>> GetByIdAsync(long id, CancellationToken ct = default) =>
            throw new InvalidOperationException("Not read.");
    }
}
