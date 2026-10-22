using ArturRios.Data.PostgreSql;
using ArturRios.Data.Relational.Core.DependencyInjection;
using ArturRios.Heimdall.Command.Registration;
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Data.Registration;
using ArturRios.Heimdall.Data.Seeding;
using ArturRios.Heimdall.Query.Registration;
using ArturRios.Heimdall.WebApi.Documentation;
using ArturRios.Heimdall.WebApi.Metrics;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.WebApi.Configuration;
using ArturRios.Util.WebApi.Security.Extensions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Net.Http.Headers;
using Serilog;

namespace ArturRios.Heimdall.WebApi;

/// <summary>
///     Builds the API on Util.WebApi's standard sequence: configuration, controllers, the
///     invalid-model-state envelope and Swagger, then <see cref="ConfigureServices" />, then the
///     standard pipeline — forwarded headers, tracing, <c>ExceptionMiddleware</c>, Swagger, CORS and
///     <c>AuthenticationMiddleware</c>, in that order — and the controllers.
/// </summary>
/// <remarks>
///     What the standard pipeline has no place for is added around it rather than inside it. The
///     metrics branch, forwarded headers, the developer exception page and HTTPS redirection go ahead
///     of it, through <see cref="EdgePipeline" />; rate limiting goes after it, in
///     <see cref="CreateApplication" />.
///     Call <see cref="CreateApplication" />, not <c>Build</c>, to get a complete API.
/// </remarks>
public class Startup : WebApiStartup
{
    private const string CorsAllowedOriginsEnvironmentVariable = "HEIMDALL_CORS_ALLOWED_ORIGINS";
    private const string CorsPolicyName = "HeimdallFrontEnds";

    /// <summary>
    ///     Read in <see cref="AddMetrics" />, after <c>LoadConfiguration</c> has put the .env file's
    ///     values in the environment, and used again by <see cref="EdgePipeline" /> to mount the scrape
    ///     endpoint — one reading for both, so the exporter and its endpoint cannot disagree.
    /// </summary>
    private MetricsOptions _metrics = MetricsOptions.Disabled;

    /// <summary>
    ///     Decided once in <see cref="AddDependencies" /> (NFR-28) and read by every place that turns a
    ///     sensitive diagnostic on — EF Core's options, <see cref="EdgePipeline" />'s developer exception
    ///     page and, for the page the host adds by itself in Development,
    ///     <see cref="SuppressedDeveloperPageFilter" /> — so they can never disagree. Everything off until
    ///     it is decided.
    /// </summary>
    private SensitiveDiagnostics _diagnostics = SensitiveDiagnostics.None;

    public Startup(string[] args) : base(args, ConfigureStandardSequence)
    {
        // Before anything else, so every later step has somewhere to log — which is why
        // LoggingConfiguration reads its settings straight from the environment (see its remarks).
        LoggingConfiguration.Configure();

        Builder.Host.UseSerilog();

        Log.Information("Hello world!");
        Log.Information("Building web api on {EnvironmentEnvironmentName} environment", Builder.Environment.EnvironmentName);
    }

    /// <summary>
    ///     Tunes the parts of the standard sequence Heimdall does not take as they come.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Swagger is described by <see cref="SwaggerConfiguration" /> alone, the same method
    ///         tools/ArturRios.Heimdall.OpenApiGen applies to produce docs/openapi/heimdall.json, so the
    ///         published page and the running API's are one document. Util.WebApi's own JWT scheme is
    ///         left off for that reason: <see cref="SwaggerConfiguration" /> defines the "Bearer" scheme
    ///         and the document-wide requirement itself, and the library's would collide with it — its
    ///         <c>AddSecurityDefinition</c> throws on a duplicate key, and it runs after this callback.
    ///     </para>
    ///     <para>
    ///         This does not expose the document in production: Util.WebApi registers Swagger only in
    ///         the environments it allows, and in Production it registers nothing at all.
    ///     </para>
    ///     <para>
    ///         The client IP address goes in every request's log entry. That is the library's default,
    ///         set here all the same because it is a decision about personal data, not a technicality:
    ///         the Privacy Notice (1.2) and the Data Retention Schedule declare it, and turning it off
    ///         would make them say more than the API does.
    ///     </para>
    ///     <para>
    ///         The same address is tagged on the request's activity as <c>client.address</c>, also the
    ///         library's default and also set here, for the same reason. Nothing exports traces, so
    ///         today the tag lives and dies with the activity in memory; adding a trace exporter would
    ///         carry the address out of the process, and that is a change to the Privacy Notice and the
    ///         Data Retention Schedule before it is a change here.
    ///     </para>
    /// </remarks>
    private static void ConfigureStandardSequence(WebApiStartupOptions options)
    {
        options.Swagger.ConfigureGenerator = SwaggerConfiguration.Configure;
        options.TraceActivity.LogClientIp = true;
        options.TraceActivity.TagClientAddress = true;
    }

    /// <summary>
    ///     Builds the API and completes it: <c>Build</c>'s standard sequence, then rate limiting, then
    ///     the database seed — so nothing is served before the reference data exists.
    /// </summary>
    public WebApplication CreateApplication()
    {
        var app = Build();

        Log.Information("App built successfully");

        // After the standard pipeline, because it has no slot for it. Rate limiting still sees the
        // endpoint — WebApplication routes ahead of every middleware it is given — and still runs
        // before MVC: the endpoint itself is always the last step, however late this is added. Most
        // rate-limited endpoints are anonymous, so AuthenticationMiddleware passes them through to
        // this point rather than spending anything on them first; the few authenticated ones that
        // check a password or a second factor pay only for reading the bearer token.
        app.UseRateLimiter();

        Log.Information("App configured successfully");

        SeedDatabase(app);

        Log.Information("Services started successfully");
        Log.Information("Ready to run!");

        return app;
    }

    protected override void ConfigureServices(WebApplicationBuilder builder)
    {
        Log.Information("Configuration loaded successfully");

        Builder.Services.AddHeimdallControllers();

        Log.Information("Web api configured successfully");

        AddDependencies();

        Log.Information("Dependencies added successfully");

        AddMetrics();

        builder.Services.AddSingleton<IStartupFilter>(new EdgePipeline(_metrics, _diagnostics));

        builder.Services.AddTokenSecurity();
        ConfigureCors();
        builder.Services.AddTrustedForwardedHeaders();

        Log.Information("Security configured successfully");
    }

    private void AddDependencies()
    {
        AddDatabase();

        Builder.Services.AddPersistenceServices();
        Builder.Services.AddRequestContext();
        Builder.Services.AddCommandHandlers();

        Builder.Services.AddDataRetention();

        Builder.Services.AddQueryHandlers();
        Builder.Services.AddHealthProbes();

        Builder.Services.AddAccountEmailFlows();
        Builder.Services.AddEmailSenders(Builder.Environment);
        Builder.Services.AddGoogleSignIn(Builder.Environment);

        Builder.Services.AddDataProtectionKeyRing();
        Builder.Services.AddTwoFactorServices();
        Builder.Services.AddTokenIssuing();
        Builder.Services.AddDatabaseSeeding();
    }

    private void AddDatabase()
    {
        // EF diagnostics expose parameter and column values — password hashes, salts, e-mails — so
        // they stay off in production, and off wherever the instance serves a public host unless the
        // operator keeps them on (NFR-28).
        DecideSensitiveDiagnostics();

        Builder.Services.AddSingleton(_diagnostics);

        // The developer exception page the host adds in Development answers to the same decision:
        // where the environment asks for the page but it is off, a failure that reaches it gets the
        // API's own 500 envelope. Elsewhere there is no page, so there is nothing to filter.
        if (AsksForDeveloperExceptionPage(Builder.Environment) && !_diagnostics.DeveloperExceptionPage)
        {
            Builder.Services.AddSingleton<IDeveloperPageExceptionFilter, SuppressedDeveloperPageFilter>();
        }

        Builder.Services.AddSingleton(new DbContextDiagnosticsOptions
        {
            SensitiveDataLogging = _diagnostics.DataLogging,
            DetailedErrors = _diagnostics.DataLogging
        });

        Builder.Services.AddPostgreSqlProvider();
        Builder.Services.AddDataConfigFromEnvironment<AppDbContext>("HEIMDALL_DATA");

        DatabaseConnectionGuard.WarnIfDatabaseConnectionIsNotEncrypted();
    }

    /// <summary>
    ///     Whether the environment asks for the developer exception page: Development and Local. What
    ///     is served is <see cref="_diagnostics" />, which may turn it off.
    /// </summary>
    /// <remarks>
    ///     Local is included alongside Development because it is what a developer machine now runs:
    ///     the configuration loader resolves Environments/.env.&lt;environment&gt;, so the launch
    ///     profiles name Local to reach .env.local rather than relying on the loader's fallback.
    ///     Testing <c>IsDevelopment()</c> alone would have silently cost the developer exception page
    ///     in the one environment that exists to have it.
    /// </remarks>
    private static bool AsksForDeveloperExceptionPage(IWebHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Local");

    /// <summary>
    ///     NFR-28: settles <see cref="_diagnostics" />. On an instance that serves a public host the
    ///     sensitive diagnostics are turned off, unless <c>HEIMDALL_ALLOW_SENSITIVE_DIAGNOSTICS</c> is
    ///     <c>true</c> (see <see cref="SensitiveDiagnosticsGuard" />). Either way it warns, once.
    /// </summary>
    private void DecideSensitiveDiagnostics()
    {
        _diagnostics = SensitiveDiagnosticsGuard.EvaluateFromEnvironment(
            !Builder.Environment.IsProduction(),
            AsksForDeveloperExceptionPage(Builder.Environment));

        switch (_diagnostics.Decision)
        {
            case SensitiveDiagnosticsDecision.Suppressed:
                Log.Warning(
                    "This instance serves a public host ({Hosts}), so EF Core sensitive data logging, " +
                    "detailed errors and the developer exception page are off in the {Environment} " +
                    "environment. Set {Variable}=true to keep them on where the instance holds made-up " +
                    "people only",
                    string.Join(", ", _diagnostics.PublicHosts), Builder.Environment.EnvironmentName,
                    SensitiveDiagnosticsGuard.AllowVariable);
                break;

            case SensitiveDiagnosticsDecision.Allowed:
                Log.Warning(
                    "Sensitive diagnostics are on in the {Environment} environment and this instance serves " +
                    "a public host ({Hosts}); {Variable} is true, which states that it holds made-up people only",
                    Builder.Environment.EnvironmentName, string.Join(", ", _diagnostics.PublicHosts),
                    SensitiveDiagnosticsGuard.AllowVariable);
                break;
        }
    }

    /// <summary>
    ///     Registers the OpenTelemetry metrics pipeline and its Prometheus exporter, on the port
    ///     <c>HEIMDALL_METRICS_PORT</c> names (9464 by default, <c>0</c> to switch it off).
    /// </summary>
    /// <remarks>
    ///     The scrape endpoint is recognised by the port the connection arrived on, never by the
    ///     <c>Host</c> header — Traefik forwards the client's own, so it is attacker-controlled. See
    ///     <see cref="MetricsOptions" /> for why the port is the one thing a caller cannot choose, and
    ///     the Dockerfile for how the container is made to listen on it without publishing it.
    /// </remarks>
    private void AddMetrics()
    {
        _metrics = MetricsOptions.FromEnvironment();

        if (_metrics.InvalidValue is not null)
        {
            Log.Warning(
                "Ignoring unusable {Variable} value {Value}; serving metrics on the default port {Port}",
                MetricsOptions.PortVariable, _metrics.InvalidValue, _metrics.Port);
        }

        if (!_metrics.Enabled)
        {
            Log.Warning(
                "Metrics are switched off by {Variable}; nothing will be collected or served for Prometheus",
                MetricsOptions.PortVariable);

            return;
        }

        Builder.Services.AddHeimdallMetrics(_metrics);

        Log.Information(
            "Serving Prometheus metrics at {Path} on port {Port} only",
            MetricsOptions.ScrapePath, _metrics.Port);
    }

    /// <summary>
    ///     Restricts cross-origin access to the front ends named by
    ///     <c>HEIMDALL_CORS_ALLOWED_ORIGINS</c> (comma-separated, scheme and host as the browser sends
    ///     them, e.g. <c>https://app.example.com</c>). With the variable unset, no cross-origin
    ///     request is allowed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This used to be <c>AllowAnyOrigin</c> in every environment. The same-origin policy is
    ///         the control that stops a page on an unrelated origin from reading an authenticated
    ///         response, and switching it off system-wide is a poor trade for an identity API: any
    ///         site the caller visits could then read <c>/api/persons/{id}</c> with a token it
    ///         scraped, and drive the anonymous endpoints from every visitor's browser at once.
    ///     </para>
    ///     <para>
    ///         The policy is registered here and applied by the standard pipeline, which names it in
    ///         <c>UseCors</c> ahead of <c>AuthenticationMiddleware</c>, so a preflight request is
    ///         answered without a token. With no origins configured no policy is named, and the
    ///         pipeline adds no CORS middleware at all.
    ///     </para>
    ///     <para>
    ///         Refusing by default rather than falling back to the wildcard is deliberate, and it is
    ///         the <c>UnconfiguredGoogleIdTokenVerifier</c> judgement rather than the e-mail sender's:
    ///         a missing CORS entry costs a browser-based front end its access until an operator adds
    ///         one, which is visible and quickly fixed, while defaulting to "any origin" would leave a
    ///         deployment wide open with nothing to indicate it. Server-to-server callers are
    ///         unaffected — CORS is a browser rule and non-browser clients never send an Origin.
    ///     </para>
    /// </remarks>
    private void ConfigureCors()
    {
        var origins = (Environment.GetEnvironmentVariable(CorsAllowedOriginsEnvironmentVariable) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (origins.Length == 0)
        {
            Log.Warning(
                "No cross-origin front end is configured ({Variable}); every cross-origin request " +
                "will be refused by the browser's same-origin policy",
                CorsAllowedOriginsEnvironmentVariable);

            return;
        }

        Log.Information("Allowing cross-origin requests from {Origins}", origins);

        // Credentials are allowed because the front end sends the bearer token UC-11 issued. That is
        // also why the origin list has to be explicit: AllowAnyOrigin and AllowCredentials are
        // mutually exclusive by specification, precisely to stop this combination from existing.
        // Retry-After is exposed because it is not a safelisted response header: without it a front
        // end on another origin sees a 429 but cannot read how long to wait.
        Builder.Services.AddCors(cors => cors.AddPolicy(CorsPolicyName, policy => policy
            .WithOrigins(origins)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials()
            .WithExposedHeaders(HeaderNames.RetryAfter)));

        Options.CorsPolicy = CorsPolicyName;
    }

    /// <summary>
    ///     Runs the database seeder before the host starts serving, so the reference data the
    ///     application depends on is guaranteed to exist. Migrations are not applied here — the
    ///     seeder throws if any are pending.
    /// </summary>
    private static void SeedDatabase(WebApplication app)
    {
        using var scope = app.Services.CreateScope();

        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();

        seeder.SeedAsync().GetAwaiter().GetResult();
    }
}
