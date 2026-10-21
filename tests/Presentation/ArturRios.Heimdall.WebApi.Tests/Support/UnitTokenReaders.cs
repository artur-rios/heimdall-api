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
///     ES256 configuration built through <see cref="TokenSigningKeys.ApplyTo" />.
/// </summary>
/// <remarks>
///     Every token is timed against one fixed <see cref="Now" />, taken when the readers are built, in
///     whole seconds as a token's NumericDate claims are, so its times do not drift while a test runs.
///     <see cref="TokenFor" /> mints, for each reader, a token right in every respect but the key it is
///     signed with, live now even under the challenge token's ten-minute lifetime.
/// </remarks>
public sealed class UnitTokenReaders
{
    /// <summary>The issuer every token is issued under unless a test names another.</summary>
    public const string DefaultIssuer = "heimdall-tests";

    /// <summary>The person audience unless a test names another.</summary>
    public const string DefaultPersonAudience = "heimdall-tests";

    /// <summary>The application audience.</summary>
    public const string ApplicationAudience = "heimdall-tests-applications";

    /// <summary>The bearer reader's name: <see cref="PersonTokenValidator" />.</summary>
    public const string BearerReader = "bearer";

    /// <summary>The challenge reader's name: <see cref="JwtTwoFactorChallengeTokenIssuer" />.</summary>
    public const string ChallengeReader = "challenge";

    /// <summary>The application reader's name: <see cref="JwtApplicationTokenService" />.</summary>
    public const string ApplicationReader = "application";

    /// <summary>A person token's lifetime: <c>HEIMDALL_AUTH_TOKEN_EXPIRATION_IN_SECONDS</c>'s default.</summary>
    public static readonly TimeSpan PersonLifetime = TimeSpan.FromHours(1);

    /// <summary>An application token's lifetime: twenty minutes, distinct from both other kinds'.</summary>
    public static readonly TimeSpan ApplicationLifetime = TimeSpan.FromMinutes(20);

    /// <summary>The person every person-shaped token names.</summary>
    public readonly IdentityUser Person = new(Guid.NewGuid(), (int)Roles.SystemAdmin) { SecurityStamp = Guid.NewGuid() };

    /// <summary>The challenge every challenge-shaped token names.</summary>
    public readonly Guid ChallengeId = Guid.NewGuid();

    /// <summary>The application every application-shaped token names.</summary>
    public readonly ApplicationTokenSubject Subject = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Billing");

    /// <param name="keys">The signing keys; omitted, a fresh key and no previous one.</param>
    /// <param name="issuer">The configured issuer.</param>
    /// <param name="personAudience">The configured person audience.</param>
    public UnitTokenReaders(
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

        Bearer = new PersonTokenValidator(
            new JwtTokenValidator(Configuration, handler, Mapper, options), Configuration, Audiences);
        Challenge = new JwtTwoFactorChallengeTokenIssuer(Configuration, Audiences, handler, Mapper);
        Application = new JwtApplicationTokenService(Configuration, handler, ApplicationOptions);
        AuthIssuer = new JwtAuthTokenIssuer(Configuration, Audiences, handler, Mapper, new UnreadPermissions());
    }

    /// <summary>The instant every token is timed against: when the readers were built, in whole seconds.</summary>
    public DateTimeOffset Now { get; } = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public TokenSigningKeys Keys { get; }

    /// <summary>The ES256 configuration every issuer and reader is given.</summary>
    public JwtConfiguration Configuration { get; }

    public PersonTokenAudiences Audiences { get; }

    public IdentityUserMapper Mapper { get; }

    public ApplicationTokenOptions ApplicationOptions { get; }

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

    /// <summary>An instant relative to <see cref="Now" />.</summary>
    public DateTime At(TimeSpan fromNow) => Now.Add(fromNow).UtcDateTime;

    /// <summary>
    ///     The claims and the audience a token must carry to be read by <paramref name="reader" />
    ///     (<see cref="BearerReader" />, <see cref="ChallengeReader" /> or <see cref="ApplicationReader" />):
    ///     the person's under the person audience, the challenge's under the challenge audience, or the
    ///     application's under its own.
    /// </summary>
    public (Dictionary<string, object> Claims, string Audience) PayloadFor(string reader) => reader switch
    {
        BearerReader => (ClaimsOf(Mapper.ToClaims(Person)), Audiences.Person),
        ChallengeReader => (ClaimsOf(Mapper.ToClaims(
            new IdentityUser(Person.Id, Person.RoleId)
            {
                MfaPending = true, ChallengeId = ChallengeId, SecurityStamp = Person.SecurityStamp
            })), Audiences.TwoFactorChallenge),
        ApplicationReader => (new Dictionary<string, object>
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
    ///     minutes before <see cref="Now" />, and expiring eight minutes after it, within every kind's
    ///     lifetime. Only the key can then make a reader refuse it.
    /// </summary>
    public string TokenFor(string reader, SigningCredentials credentials, string? issuer = null)
    {
        var (claims, audience) = PayloadFor(reader);

        return UnitSigningKeys.TokenAt(
            credentials,
            claims,
            issuer ?? Configuration.Issuer,
            audience,
            At(TimeSpan.FromMinutes(-3)),
            At(TimeSpan.FromMinutes(-3)),
            At(TimeSpan.FromMinutes(8)));
    }

    /// <summary>
    ///     Whether <paramref name="reader" /> accepts <paramref name="token" />: the bearer reader resolves
    ///     the person, the challenge reader returns the challenge, the application reader the
    ///     application, each exactly as <see cref="PayloadFor" /> names them.
    /// </summary>
    public async Task<bool> AcceptsAsync(string reader, string token) => reader switch
    {
        BearerReader =>
            (await Bearer.ValidateAsync(token, new DefaultHttpContext())).User?.Id == Person.Id,
        ChallengeReader =>
            await Challenge.ValidateAsync(token) ==
            new TwoFactorChallengePrincipal(Person.Id, ChallengeId, Person.SecurityStamp),
        ApplicationReader =>
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
        BearerReader => (await Bearer.ValidateAsync(token, new DefaultHttpContext())).User is null,
        ChallengeReader => await Challenge.ValidateAsync(token) is null,
        ApplicationReader => await Application.ValidateAsync(token) is null,
        _ => throw new ArgumentOutOfRangeException(nameof(reader), reader, null)
    };

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
