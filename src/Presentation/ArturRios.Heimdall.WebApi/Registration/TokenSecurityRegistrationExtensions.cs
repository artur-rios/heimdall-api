using ArturRios.Data.Relational.Core.DependencyInjection;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Jwt;
using ArturRios.Util.WebApi.Configuration;
using ArturRios.Util.WebApi.Security.Authentication;
using ArturRios.Util.WebApi.Security.Enums;
using ArturRios.Util.WebApi.Security.Extensions;
using ArturRios.Util.WebApi.Security.Interfaces;
using FluentValidation;
using Serilog;

namespace ArturRios.Heimdall.WebApi;

public static class TokenSecurityRegistrationExtensions
{
    /// <summary>
    ///     The shared HS256 secret and the previous one, which 3.x still verified Heimdall's earlier
    ///     tokens with and 4.0.0 retired (Threat Model TH-43). Their values are never read: start-up
    ///     only warns while either is set, so the operator removes it.
    /// </summary>
    private static readonly string[] RetiredTokenSecretVariables =
        ["HEIMDALL_AUTH_TOKEN_SECRET", "HEIMDALL_AUTH_TOKEN_SECRET_PREVIOUS"];

    /// <summary>
    ///     Puts <see cref="PersonTokenValidator" /> in the place of the <see cref="JwtTokenValidator" />
    ///     that <c>AddTokenAuthentication</c> registers as an <see cref="ITokenValidator" />, keeping the
    ///     library's validator as the inner one it wraps.
    /// </summary>
    /// <remarks>
    ///     TB-5: the library's validator checks the signature and lifetime but not the issuer or the
    ///     audience, and Heimdall's key signs every kind of token. PersonTokenValidator wraps it and
    ///     takes its place, so a token reaches the middleware's user only if Heimdall issued it, for
    ///     one of the person audiences. When the library registers its validator in some other way,
    ///     start-up stops and says why, rather than running with the library's validator alone.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     The services hold no <see cref="ITokenValidator" /> registered as <see cref="JwtTokenValidator" />.
    /// </exception>
    public static void ReplaceLibraryTokenValidator(IServiceCollection services)
    {
        var libraryValidator = services.SingleOrDefault(descriptor =>
            descriptor.ServiceType == typeof(ITokenValidator) &&
            descriptor.ImplementationType == typeof(JwtTokenValidator));

        if (libraryValidator is null)
        {
            throw new InvalidOperationException(
                "ArturRios.Util.WebApi no longer registers JwtTokenValidator as an ITokenValidator; " +
                "PersonTokenValidator cannot replace it. Refusing to start.");
        }

        services.Remove(libraryValidator);
        services.AddSingleton<JwtTokenValidator>();
        services.AddSingleton<ITokenValidator, PersonTokenValidator>();
    }

    /// <summary>
    ///     Registers token signing, the person and application token services and the token
    ///     authentication, then the rate limiting and the password hash gate that sit beside them.
    /// </summary>
    public static IServiceCollection AddTokenSecurity(this IServiceCollection services)
    {
        // Authentication is Util.WebApi's alone: ASP.NET Core's authentication and authorization
        // middlewares are not registered, because nothing here reads HttpContext.User or carries
        // ASP.NET Core's [Authorize] — the controllers use the library's attributes, which read the
        // user AuthenticationMiddleware attaches.
        //
        // AuthenticationMiddleware resolves AuthenticationOptions and the token validators from the
        // container, and JwtTokenValidator additionally needs JwtConfiguration, JwtHandler, and the
        // claims mapper. IdentityUserMapper replaces the library default so tokens carry PublicIds
        // and the scope claims of FR-AU-04. Defaults are kept otherwise: app JWT only, read from the
        // Authorization header, user rebuilt from claims — so no IAuthenticationProvider is required
        // and no database read happens per request.
        //
        // Every token is signed ES256 with the current key; a missing or unusable key stops start-up
        // here, naming the variable. HS256 is refused: the shared secret is retired (TH-43), and its
        // variables are only looked at to tell the operator to remove them.
        var signingKeys = TokenSigningKeys.FromEnvironment();

        var jwtConfiguration = JwtConfigurationBuilder.BuildJwtConfiguration(signingKeys);
        services.AddSingleton(signingKeys);
        services.AddSingleton(jwtConfiguration);
        services.AddSingleton<JwtHandler>();

        // Thumbprints only, which are public: they are what the published key set and every token's
        // kid carry, so a consumer can be checked against them. No key material is logged.
        Log.Information(
            "Tokens: signed ES256 with key {KeyId}; published keys {KeyIds}; HS256 tokens {LegacyState}",
            signingKeys.Current.Id,
            signingKeys.ConfigurationKeys.Select(key => key.Id).ToArray(),
            "refused");

        WarnOnRetiredTokenSecrets();

        // TH-06/TH-35: the challenge and subject-rights-only tokens carry audiences derived from the
        // person audience, so a consumer pinning it refuses them. Logged, like the application
        // audience below, so a consumer can be checked against them.
        var personTokenAudiences = PersonTokenAudiences.From(jwtConfiguration);
        services.AddSingleton(personTokenAudiences);
        Log.Information(
            "Person tokens: audience {Audience}; challenge tokens {ChallengeAudience}; " +
            "subject-rights-only tokens {SubjectRightsAudience}",
            personTokenAudiences.Person, personTokenAudiences.TwoFactorChallenge,
            personTokenAudiences.SubjectRights);

        // UC-56/UC-57: application tokens share the signing keys but not the audience. Read here,
        // with the rest of the token configuration, so a missing or equal audience stops start-up
        // before anything is served (TH-35).
        var applicationTokens = ApplicationTokenOptions.FromEnvironment();
        services.AddSingleton(applicationTokens);
        ConfigurationWarnings.WarnOnFallback(ApplicationTokenOptions.ExpirationVariable, applicationTokens.RejectedExpiration,
            ApplicationTokenOptions.DefaultLifetime.TotalSeconds);
        services.AddScoped<JwtApplicationTokenService>();
        services.AddScoped<IApplicationTokenIssuer>(provider =>
            provider.GetRequiredService<JwtApplicationTokenService>());
        services.AddScoped<IApplicationTokenValidator>(provider =>
            provider.GetRequiredService<JwtApplicationTokenService>());

        // The audience is not a secret; logging it makes a mismatch with a consumer diagnosable.
        Log.Information(
            "Application tokens: audience {Audience}, lifetime {Seconds}s",
            applicationTokens.Audience, applicationTokens.Lifetime.TotalSeconds);

        services.AddTokenAuthentication<IdentityUserMapper>(options =>
        {
            options.Source = TokenSource.Header;
            options.EnableJwt = true;
            options.EnableGoogle = false;
            options.JwtMode = JwtValidationMode.ClaimsOnly;
        });

        ReplaceLibraryTokenValidator(services);

        services.AddAuthEndpointRateLimiting();

        // TH-03: bound how many Argon2id derivations run at once. Read here, with the rest of the
        // security configuration, because it belongs to the same defence: the rate limiter caps what
        // one address can ask for, and this caps what the process will attempt regardless of how
        // many addresses ask.
        PasswordHashGate.ConfigureSharedFromEnvironment();

        return services;
    }

    /// <summary>
    ///     One Warning for each retired token secret variable that is still set, naming it and never its
    ///     value, so the operator removes it. Nothing else reads them: a value 3.x refused to start
    ///     with no longer stops start-up either.
    /// </summary>
    private static void WarnOnRetiredTokenSecrets()
    {
        foreach (var variable in SetRetiredTokenSecretVariables(Environment.GetEnvironmentVariable))
        {
            Log.Warning("{Variable} is set but no longer read; remove it", variable);
        }
    }

    /// <summary>
    ///     The retired token secret variables <paramref name="read" /> finds set. A value that is empty
    ///     or whitespace only is unset, as 3.x treated it: an env file's <c>VARIABLE=</c> reaches the
    ///     process as the empty string.
    /// </summary>
    public static IReadOnlyList<string> SetRetiredTokenSecretVariables(Func<string, string?> read) =>
        [.. RetiredTokenSecretVariables.Where(variable => !string.IsNullOrWhiteSpace(read(variable)))];
}
