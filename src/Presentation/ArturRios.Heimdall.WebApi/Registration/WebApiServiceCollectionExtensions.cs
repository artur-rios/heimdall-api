using ArturRios.Data.Relational.Core.DependencyInjection;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.HealthChecks;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Heimdall.WebApi.Binding;
using ArturRios.Heimdall.WebApi.Monitoring;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Util.WebApi.Configuration;
using ArturRios.Util.WebApi.Security.Extensions;

namespace ArturRios.Heimdall.WebApi;

public static class WebApiServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the accessors that read the acting person and the retry hint from the current HTTP
    ///     request.
    /// </summary>
    public static IServiceCollection AddRequestContext(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<IActorAccessor, HttpContextActorAccessor>();
        services.AddScoped<IRetryAfterAdvisor, HttpResponseRetryAfterAdvisor>();

        return services;
    }

    /// <summary>
    ///     Registers the health checks (UC-30) and the readiness probe's cache (#215).
    /// </summary>
    public static IServiceCollection AddHealthProbes(this IServiceCollection services)
    {
        // Health checks (UC-30). Each IServiceHealthCheck is one verified dependency; the detailed
        // handler resolves them all as IEnumerable, so new checks are added by registering another.
        // The readiness probe (#215) asks the database check alone, so it is also resolvable as itself.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ReadinessCache>();
        services.AddScoped<DatabaseHealthCheck>();
        services.AddScoped<IServiceHealthCheck>(provider => provider.GetRequiredService<DatabaseHealthCheck>());
        services
            .AddScoped<IQueryHandlerAsync<DetailedHealthQuery, HealthCheckOutput>, GetDetailedHealthQueryHandler>();

        return services;
    }

    /// <summary>
    ///     Registers the person, challenge and ownership services that issue and check tokens.
    /// </summary>
    public static IServiceCollection AddTokenIssuing(this IServiceCollection services)
    {
        services.AddScoped<IScopeOwnershipChecker, ScopeOwnershipChecker>();

        // UC-11 issues tokens through the same claims mapper the middleware validates them with,
        // registered by AddTokenAuthentication in TokenSecurityRegistrationExtensions.AddTokenSecurity.
        services.AddScoped<IAuthTokenIssuer, JwtAuthTokenIssuer>();
        // Shared by LoginCommandHandler (UC-11) and VerifyTwoFactorAuthCommandHandler (UC-38) so the
        // scope-eligibility rules and the final token-issuing call live in one place.
        services.AddScoped<PersonAuthTokenService>();
        // UC-38's challenge token (FR-2F-07…FR-2F-10, NFR-17): one class both issues and validates
        // it, registered once and exposed through both of its interfaces.
        services.AddScoped<JwtTwoFactorChallengeTokenIssuer>();
        services.AddScoped<ITwoFactorChallengeTokenIssuer>(
            provider => provider.GetRequiredService<JwtTwoFactorChallengeTokenIssuer>());
        services.AddScoped<ITwoFactorChallengeTokenValidator>(
            provider => provider.GetRequiredService<JwtTwoFactorChallengeTokenIssuer>());

        return services;
    }

    /// <summary>
    ///     Adds the MVC configuration Heimdall applies on top of the standard sequence's controllers.
    /// </summary>
    public static IServiceCollection AddHeimdallControllers(this IServiceCollection services)
    {
        // The standard sequence has already called AddControllers(); calling it again adds this
        // configuration to the same MVC registration.
        //
        // MfaPendingGuardFilter (FR-2F-10, NFR-17) runs as a global MVC authorization filter, on
        // every controller action — added here rather than per-action, since a UC-38 challenge token
        // must be rejected everywhere except POST /api/auth/2fa/verify, and that endpoint needs no
        // opt-out: it never reads the challenge token as a bearer credential to begin with.
        services.AddControllers(options =>
        {
            options.Filters.Add<MfaPendingGuardFilter>();

            // NFR-24: a subject-rights-only token, issued to a restricted identity at sign-in, is
            // confined to the data-subject-rights actions — on its claims alone, so before the
            // liveness check's database read, like the guard above.
            options.Filters.Add<SubjectRightsOnlyGuardFilter>();

            // Runs after the guard above, which is the cheap check: a challenge token misused as a
            // bearer credential is rejected on its claims alone, without spending the database read
            // this one makes. Global for the same reason — a token naming a deleted identity must be
            // refused everywhere, and leaving that to each handler is what let a logically deleted
            // System Admin keep acting until their token expired.
            options.Filters.Add<ActorLivenessFilter>();

            // Turns a saturated password-hash gate into 503 rather than 500 (TH-03). An exception
            // filter because the condition is cross-cutting: every endpoint that derives a password
            // can meet it, and each has its own message map.
            options.Filters.Add<PasswordHashSaturationFilter>();

            // A server-side line for every 5xx the pipeline produces as a result, which nothing else
            // writes (#227); an unhandled exception is already logged by ExceptionMiddleware.
            options.Filters.Add<ServerErrorLoggingFilter>();

            // NFR-10 (#186): a request MVC could not bind answers 400 instead of running the action with
            // a null or default argument. An action filter, so after the authorization filters above —
            // an unauthenticated caller still gets 401 — and after model binding has filled ModelState.
            options.Filters.Add<MalformedRequestFilter>();

            // Also applied by ArturRios.Heimdall.OpenApiGen/Program.cs, which builds its own
            // AddControllers() rather than running this Startup, so that call site cannot catch a
            // removal here. Without it, [FromQuery] list-query properties marked server-populated
            // would become bindable from the query string again on the running API — the controllers
            // still overwrite them before use, so the query-string forgery tests would keep passing,
            // and the published document (generated from the other call site) would look unchanged.
            // The guarantee that these properties are non-bindable, not merely undocumented, would
            // silently revert with no test catching it.
            ModelBindingConfiguration.Configure(options);
        });
        services.AddEndpointsApiExplorer();

        return services;
    }
}
