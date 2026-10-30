using ArturRios.Data.Relational.Core.DependencyInjection;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.WebApi.Configuration;
using ArturRios.Util.WebApi.Security.Extensions;
using Serilog;

namespace ArturRios.Heimdall.WebApi;

public static class GoogleSignInRegistrationExtensions
{
    /// <summary>
    ///     Chooses how UC-25 verifies a Google ID token (FR-GO-11, NFR-13). With Google client IDs
    ///     configured, tokens are validated against Google; without them, every token is refused, so
    ///     an unconfigured deployment answers 401 rather than trusting a token no one checked.
    /// </summary>
    /// <remarks>
    ///     The third branch exists for the functional suite, which cannot override a DI registration
    ///     (<c>WebApiTest&lt;T&gt;</c> exposes neither its factory nor a settable gateway) and must
    ///     still reach the flows behind verification. It is guarded twice — never in Production, and
    ///     never without an explicitly set signing secret — and is checked before the real verifier so
    ///     a test environment cannot accidentally run both. See <see cref="LocalGoogleIdTokenVerifier" />.
    /// </remarks>
    public static IServiceCollection AddGoogleSignIn(this IServiceCollection services, IHostEnvironment environment)
    {
        var options = GoogleSignInOptions.FromEnvironment();

        services.AddSingleton(options);

        if (!environment.IsProduction() && options.TestSigningConfigured)
        {
            Log.Warning(
                "Google ID tokens will be verified against a local signing secret ({Variable}), not " +
                "against Google. This is for automated tests only",
                GoogleSignInOptions.TestSigningSecretVariable);

            services.AddScoped<IGoogleIdTokenVerifier, LocalGoogleIdTokenVerifier>();

            return services;
        }

        if (!options.GoogleConfigured)
        {
            Log.Warning(
                "No Google client is configured ({Variable}); Google sign-in (UC-25) will refuse " +
                "every token",
                GoogleSignInOptions.ClientIdsVariable);

            services.AddScoped<IGoogleIdTokenVerifier, UnconfiguredGoogleIdTokenVerifier>();

            return services;
        }

        services.AddScoped<IGoogleIdTokenVerifier, GoogleIdTokenVerifier>();

        return services;
    }
}
