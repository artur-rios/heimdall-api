using System.Net;
using System.Net.Sockets;
using ArturRios.Data.PostgreSql;
using ArturRios.Data.Relational.Core.DependencyInjection;
using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Data.Persistence;
using ArturRios.Heimdall.Data.Seeding;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.HealthChecks;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Input.Validation;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Heimdall.WebApi.Binding;
using ArturRios.Heimdall.WebApi.Documentation;
using ArturRios.Heimdall.WebApi.Email;
using ArturRios.Heimdall.WebApi.Metrics;
using ArturRios.Heimdall.WebApi.Monitoring;
using ArturRios.Heimdall.WebApi.Retention;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Jwt;
using ArturRios.Messaging.Email;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Query;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Util.WebApi.Configuration;
using Microsoft.AspNetCore.DataProtection;
using FluentValidation;
using Microsoft.AspNetCore.HttpOverrides;
using ArturRios.Util.WebApi.Security.Enums;
using ArturRios.Util.WebApi.Security.Extensions;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Rewrite;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;
using System.Threading.RateLimiting;

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
    /// <summary>
    ///     Set to <c>true</c> to refuse start-up when the database connection does not require TLS,
    ///     rather than warning. Opt-in: see <see cref="WarnIfDatabaseConnectionIsNotEncrypted" />.
    /// </summary>
    private const string RequireDatabaseTlsVariable = "HEIMDALL_DATA_REQUIRE_TLS";

    private const string LogDirectoryEnvironmentVariable = "HEIMDALL_LOG_DIRECTORY";
    private const string DefaultLogDirectory = "logs";

    private const string TokenAudienceEnvironmentVariable = "HEIMDALL_AUTH_TOKEN_AUDIENCE";
    private const string TokenExpirationEnvironmentVariable = "HEIMDALL_AUTH_TOKEN_EXPIRATION_IN_SECONDS";
    private const string TokenIssuerEnvironmentVariable = "HEIMDALL_AUTH_TOKEN_ISSUER";
    private const string TokenSecretEnvironmentVariable = "HEIMDALL_AUTH_TOKEN_SECRET";
    private const string PreviousTokenSecretEnvironmentVariable = "HEIMDALL_AUTH_TOKEN_SECRET_PREVIOUS";
    private const double DefaultTokenExpirationInSeconds = 3600;

    private const string CorsAllowedOriginsEnvironmentVariable = "HEIMDALL_CORS_ALLOWED_ORIGINS";
    private const string CorsPolicyName = "HeimdallFrontEnds";

    /// <summary>
    ///     Rate-limiting policy name applied via <c>[EnableRateLimiting(AuthEndpointRateLimitPolicy)]</c>
    ///     to <c>AuthController</c>'s anonymous, credential-checking endpoints. Public so the
    ///     controller and this configuration stay in sync without duplicating the literal.
    /// </summary>
    public const string AuthEndpointRateLimitPolicy = "AuthAnonymous";

    /// <summary>
    ///     Read in <see cref="AddMetrics" />, after <c>LoadConfiguration</c> has put the .env file's
    ///     values in the environment, and used again by <see cref="EdgePipeline" /> to mount the scrape
    ///     endpoint — one reading for both, so the exporter and its endpoint cannot disagree.
    /// </summary>
    private MetricsOptions _metrics = MetricsOptions.Disabled;

    public Startup(string[] args) : base(args, ConfigureStandardSequence)
    {
        // Before anything else, so every later step has somewhere to log — which is why
        // ConfigureLogging reads its settings straight from the environment (see its remarks).
        ConfigureLogging();

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

        ConfigureWebApi();

        Log.Information("Web api configured successfully");

        AddDependencies();

        Log.Information("Dependencies added successfully");

        AddMetrics();

        builder.Services.AddSingleton<IStartupFilter>(new EdgePipeline(_metrics, builder.Environment));

        ConfigureSecurity();
        ConfigureCors();
        ConfigureForwardedHeaders();

        Log.Information("Security configured successfully");
    }

    private void AddDependencies()
    {
        // EF diagnostics expose parameter and column values — password hashes, salts, e-mails — so
        // they stay off in production.
        var diagnosticsEnabled = !Builder.Environment.IsProduction();

        Builder.Services.AddSingleton(new DbContextDiagnosticsOptions
        {
            SensitiveDataLogging = diagnosticsEnabled,
            DetailedErrors = diagnosticsEnabled
        });

        Builder.Services.AddPostgreSqlProvider();
        Builder.Services.AddDataConfigFromEnvironment<AppDbContext>("HEIMDALL_DATA");

        WarnIfDatabaseConnectionIsNotEncrypted();

        // Spending single-use credentials and charging bounded budgets as one conditional UPDATE
        // each, so concurrent requests cannot both spend one code or lose each other's counts.
        Builder.Services.AddScoped<IAtomicWrites, AtomicWrites>();

        Builder.Services.AddScoped<CommandMediator>();
        Builder.Services.AddHttpContextAccessor();
        Builder.Services.AddScoped<IActorAccessor, HttpContextActorAccessor>();
        Builder.Services.AddScoped<IAuditLogWriter, AuditLogWriter>();
        Builder.Services.AddScoped<IValidator<CreateScopeCommand>, CreateScopeCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<CreateScopeCommand, CreateScopeCommandOutput, CreateScopeCommandHandler>();
        Builder.Services.AddScoped<IValidator<UpdateScopeCommand>, UpdateScopeCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<UpdateScopeCommand, UpdateScopeCommandOutput, UpdateScopeCommandHandler>();
        Builder.Services.AddAuditedCommandHandler<DeleteScopeCommand, DeleteScopeCommandOutput, DeleteScopeCommandHandler>();
        Builder.Services.AddAuditedCommandHandler<HardDeleteScopeCommand, HardDeleteScopeCommandOutput, HardDeleteScopeCommandHandler>();
        // UC-24 does have a validator despite carrying a single field: Enabled is nullable so an
        // omitted value is refused (NFR-10) rather than binding to false and disabling the setting.
        Builder.Services.AddScoped<IValidator<SetGoogleSignInCommand>, SetGoogleSignInCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<SetGoogleSignInCommand, SetGoogleSignInCommandOutput, SetGoogleSignInCommandHandler>();
        Builder.Services.AddScoped<IValidator<CreateAdminCommand>, CreateAdminCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<CreateAdminCommand, CreatePersonCommandOutput, CreateAdminCommandHandler>();
        Builder.Services.AddScoped<IValidator<CreateUserCommand>, CreateUserCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<CreateUserCommand, CreatePersonCommandOutput, CreateUserCommandHandler>();
        Builder.Services.AddScoped<IValidator<CreateScopeOwnerCommand>, CreateScopeOwnerCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<CreateScopeOwnerCommand, CreatePersonCommandOutput, CreateScopeOwnerCommandHandler>();
        // No validator: UC-21's request carries no body — both identifiers are route values already
        // constrained to GUIDs — so there is nothing left for NFR-10 to validate.
        Builder.Services.AddAuditedCommandHandler<AddScopeOwnerCommand, AddScopeOwnerCommandOutput, AddScopeOwnerCommandHandler>();
        // Likewise no validator for UC-22: both identifiers are route values.
        Builder.Services.AddAuditedCommandHandler<RemoveScopeOwnerCommand, RemoveScopeOwnerCommandOutput, RemoveScopeOwnerCommandHandler>();
        // Likewise no validator for UC-23: both identifiers are route values.
        Builder.Services.AddAuditedCommandHandler<PromoteScopeUserCommand, PromoteScopeUserCommandOutput, PromoteScopeUserCommandHandler>();
        Builder.Services.AddScoped<IValidator<UpdatePersonCommand>, UpdatePersonCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<UpdatePersonCommand, UpdatePersonCommandOutput, UpdatePersonCommandHandler>();
        Builder.Services.AddAuditedCommandHandler<DeletePersonCommand, DeletePersonCommandOutput, DeletePersonCommandHandler>();
        Builder.Services.AddAuditedCommandHandler<HardDeletePersonCommand, HardDeletePersonCommandOutput, HardDeletePersonCommandHandler>();
        Builder.Services.AddScoped<IValidator<LoginCommand>, LoginCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<LoginCommand, LoginCommandOutput, LoginCommandHandler>();
        Builder.Services.AddScoped<IValidator<PasswordRecoveryCommand>, PasswordRecoveryCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<PasswordRecoveryCommand, PasswordRecoveryCommandOutput, PasswordRecoveryCommandHandler>();
        Builder.Services.AddScoped<IValidator<ResetPasswordCommand>, ResetPasswordCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<ResetPasswordCommand, ResetPasswordCommandOutput, ResetPasswordCommandHandler>();
        Builder.Services.AddScoped<IValidator<VerifyEmailCommand>, VerifyEmailCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<VerifyEmailCommand, VerifyEmailCommandOutput, VerifyEmailCommandHandler>();
        Builder.Services.AddScoped<IValidator<RequestErasureCommand>, RequestErasureCommandValidator>();
        Builder.Services
            .AddAuditedCommandHandler<RequestErasureCommand, RequestErasureCommandOutput,
                RequestErasureCommandHandler>();
        Builder.Services
            .AddAuditedCommandHandler<ExportMyDataCommand, DataExportCommandOutput,
                ExportMyDataCommandHandler>();
        Builder.Services
            .AddAuditedCommandHandler<RestrictProcessingCommand, RestrictProcessingCommandOutput,
                RestrictProcessingCommandHandler>();
        Builder.Services
            .AddAuditedCommandHandler<LiftProcessingRestrictionCommand,
                LiftProcessingRestrictionCommandOutput, LiftProcessingRestrictionCommandHandler>();
        Builder.Services
            .AddAuditedCommandHandler<ReapplyErasuresCommand, ReapplyErasuresCommandOutput,
                ReapplyErasuresCommandHandler>();
        // No validator: UC-15's request carries no caller-supplied input at all — the person comes
        // from the bearer token — so there is nothing for NFR-10 to validate.
        Builder.Services.AddAuditedCommandHandler<ResendVerificationEmailCommand, ResendVerificationEmailCommandOutput, ResendVerificationEmailCommandHandler>();
        // Likewise no validator for UC-25, for a different reason: the use case defines no 400 flow
        // and needs none, since an absent ID token fails verification (AF-25a, 401) and an empty
        // scope identifier matches no scope (AF-25b, 403).
        Builder.Services.AddAuditedCommandHandler<GoogleSignInCommand, GoogleSignInCommandOutput, GoogleSignInCommandHandler>();
        // No validator for UC-26 either, and for UC-15's reason: the sign-out request carries no
        // caller-supplied input — the Google User comes from the bearer token.
        Builder.Services.AddAuditedCommandHandler<GoogleSignOutCommand, GoogleSignOutCommandOutput, GoogleSignOutCommandHandler>();
        // UC-28 needs no validator either: both fields are typed route parameters, so there is no
        // caller-supplied input NFR-10 could reject that the route would not have refused first.
        Builder.Services.AddAuditedCommandHandler<DeleteGoogleUserCommand, DeleteGoogleUserCommandOutput, DeleteGoogleUserCommandHandler>();
        Builder.Services.AddAuditedCommandHandler<HardDeleteGoogleUserCommand, HardDeleteGoogleUserCommandOutput, HardDeleteGoogleUserCommandHandler>();
        Builder.Services.AddScoped<IValidator<CreateApplicationCommand>, CreateApplicationCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<CreateApplicationCommand, CreateApplicationCommandOutput, CreateApplicationCommandHandler>();
        Builder.Services.AddScoped<IValidator<UpdateApplicationCommand>, UpdateApplicationCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<UpdateApplicationCommand, UpdateApplicationCommandOutput, UpdateApplicationCommandHandler>();
        Builder.Services.AddAuditedCommandHandler<DeleteApplicationCommand, DeleteApplicationCommandOutput, DeleteApplicationCommandHandler>();
        Builder.Services.AddAuditedCommandHandler<HardDeleteApplicationCommand, HardDeleteApplicationCommandOutput, HardDeleteApplicationCommandHandler>();
        Builder.Services.AddScoped<IValidator<CreateScopePermissionCommand>, CreateScopePermissionCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<CreateScopePermissionCommand, CreateScopePermissionCommandOutput, CreateScopePermissionCommandHandler>();
        Builder.Services.AddScoped<IValidator<UpdateScopePermissionCommand>, UpdateScopePermissionCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<UpdateScopePermissionCommand, UpdateScopePermissionCommandOutput, UpdateScopePermissionCommandHandler>();
        Builder.Services.AddAuditedCommandHandler<DeleteScopePermissionCommand, DeleteScopePermissionCommandOutput, DeleteScopePermissionCommandHandler>();
        Builder.Services.AddAuditedCommandHandler<HardDeleteScopePermissionCommand, HardDeleteScopePermissionCommandOutput, HardDeleteScopePermissionCommandHandler>();

        AddDataRetention();

        Builder.Services.AddScoped<QueryMediator>();
        Builder.Services
            .AddScoped<IQueryHandlerAsync<GetScopeByIdQuery, ScopeOutput>, GetScopeByIdQueryHandler>();
        Builder.Services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopesQuery, ScopeOutput>, ListScopesQueryHandler>();
        Builder.Services
            .AddScoped<IQueryHandlerAsync<GetPersonByIdQuery, PersonOutput>, GetPersonByIdQueryHandler>();
        Builder.Services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopePersonsQuery, PersonOutput>, ListScopePersonsQueryHandler>();
        Builder.Services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopeOwnersQuery, PersonOutput>, ListScopeOwnersQueryHandler>();
        Builder.Services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopeAdminsQuery, PersonSummaryOutput>,
                ListScopeAdminsQueryHandler>();
        Builder.Services
            .AddScoped<IQueryHandlerAsync<GetApplicationByIdQuery, ApplicationOutput>,
                GetApplicationByIdQueryHandler>();
        Builder.Services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopeApplicationsQuery, ApplicationOutput>,
                ListScopeApplicationsQueryHandler>();
        Builder.Services
            .AddScoped<IQueryHandlerAsync<GetScopePermissionByIdQuery, ScopePermissionOutput>,
                GetScopePermissionByIdQueryHandler>();
        Builder.Services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopePermissionsQuery, ScopePermissionOutput>,
                ListScopePermissionsQueryHandler>();
        Builder.Services
            .AddScoped<IQueryHandlerAsync<GetGoogleUserByIdQuery, GoogleUserOutput>,
                GetGoogleUserByIdQueryHandler>();
        Builder.Services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopeGoogleUsersQuery, GoogleUserOutput>,
                ListScopeGoogleUsersQueryHandler>();
        Builder.Services
            .AddScoped<IPaginatedQueryHandlerAsync<ListErasureRequestsQuery, ErasureRequestOutput>,
                ListErasureRequestsQueryHandler>();
        Builder.Services
            .AddScoped<IQueryHandlerAsync<DetectSecuritySignalsQuery, SecuritySignalsOutput>,
                DetectSecuritySignalsQueryHandler>();
        Builder.Services
            .AddScoped<IValidator<ListErasureRequestsQuery>, ListErasureRequestsQueryValidator>();
        Builder.Services
            .AddScoped<IQueryHandlerAsync<GetTwoFactorStatusQuery, TwoFactorStatusOutput>,
                GetTwoFactorStatusQueryHandler>();

        // NFR-10: pagination/filter validation for every paginated list query.
        Builder.Services.AddScoped<IValidator<ListScopesQuery>, ListScopesQueryValidator>();
        Builder.Services.AddScoped<IValidator<ListScopePersonsQuery>, ListScopePersonsQueryValidator>();
        Builder.Services.AddScoped<IValidator<ListScopeOwnersQuery>, ListScopeOwnersQueryValidator>();
        Builder.Services.AddScoped<IValidator<ListScopeAdminsQuery>, ListScopeAdminsQueryValidator>();
        Builder.Services
            .AddScoped<IValidator<ListScopeApplicationsQuery>, ListScopeApplicationsQueryValidator>();
        Builder.Services
            .AddScoped<IValidator<ListScopePermissionsQuery>, ListScopePermissionsQueryValidator>();
        Builder.Services
            .AddScoped<IValidator<ListScopeGoogleUsersQuery>, ListScopeGoogleUsersQueryValidator>();

        // Health checks (UC-30). Each IServiceHealthCheck is one verified dependency; the detailed
        // handler resolves them all as IEnumerable, so new checks are added by registering another.
        Builder.Services.AddScoped<IServiceHealthCheck, DatabaseHealthCheck>();
        Builder.Services
            .AddScoped<IQueryHandlerAsync<DetailedHealthQuery, HealthCheckOutput>, GetDetailedHealthQueryHandler>();

        Builder.Services.AddSingleton(EmailVerificationOptions.FromEnvironment());
        Builder.Services.AddScoped<IEmailVerificationService, EmailVerificationService>();
        Builder.Services.AddSingleton(PasswordResetOptions.FromEnvironment());
        Builder.Services.AddScoped<IPasswordResetService, PasswordResetService>();
        // UC-12 runs in two halves (AF-12a): the handler queues, the dispatcher does the work after
        // the caller has been answered, so no address takes longer to answer than another. See
        // PasswordRecoveryQueue for why this is an in-memory queue rather than a floor or an outbox.
        Builder.Services.AddSingleton<PasswordRecoveryQueue>();
        Builder.Services.AddSingleton<IPasswordRecoveryQueue>(
            provider => provider.GetRequiredService<PasswordRecoveryQueue>());
        Builder.Services.AddScoped<PasswordRecoveryProcessor>();
        Builder.Services.AddHostedService<PasswordRecoveryDispatcher>();
        AddEmailSenders();
        AddGoogleSignIn();

        // UC-36 (FR-2F-02): the TOTP secret is encrypted at rest with ASP.NET Core's Data
        // Protection API before it is persisted.
        //
        // The key ring goes in the database, and the application name is fixed, because the default
        // is neither durable nor shared: keys land in a directory on the local filesystem that the
        // image does not persist and a second instance does not see. Either way the effect is the
        // same and it is silent — every stored TOTP secret becomes undecryptable, ITotpCodeVerifier
        // catches the CryptographicException and reports it as a wrong code, and every caller whose
        // second factor is an authenticator app is locked out of it with no indication why. That
        // survives a redeploy today only because a single container keeps its filesystem between
        // restarts; it would not survive being recreated, and it never survives a second replica
        // (NFR-06).
        Builder.Services.AddDataProtection()
            .PersistKeysToDbContext<AppDbContext>()
            .SetApplicationName("Heimdall");
        Builder.Services.AddScoped<ITotpSecretProtector, TotpSecretProtector>();
        // Shared by UC-37's confirmation and ITwoFactorFactorVerifier (UC-38/39/40): the TOTP secret,
        // the clock-drift window, and the single-use rule that keeps an app code from being replayed
        // live in one place rather than a copy per call site.
        Builder.Services.AddScoped<ITotpCodeVerifier, TotpCodeVerifier>();
        Builder.Services.AddScoped<IValidator<EnableTwoFactorAuthCommand>, EnableTwoFactorAuthCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<EnableTwoFactorAuthCommand, EnableTwoFactorAuthCommandOutput, EnableTwoFactorAuthCommandHandler>();

        // UC-37 (FR-2F-04/05): no validator — which code(s) are required depends on the pending row's
        // AppEnabled/EmailEnabled, a database read the handler alone can make.
        Builder.Services.AddAuditedCommandHandler<ConfirmTwoFactorAuthCommand, ConfirmTwoFactorAuthCommandOutput, ConfirmTwoFactorAuthCommandHandler>();

        // UC-38 (FR-2F-06…FR-2F-09): no validator either — same reason as UC-37, plus the challenge
        // token itself is validated inside the handler, the same "opaque token as a body value"
        // shape UC-13's ResetPasswordCommand uses.
        Builder.Services.AddAuditedCommandHandler<VerifyTwoFactorAuthCommand, VerifyTwoFactorAuthCommandOutput, VerifyTwoFactorAuthCommandHandler>();

        // UC-46 (FR-2F-16): no validator — the challenge token is the whole of the input and is
        // validated inside the handler, the same shape UC-38 uses.
        Builder.Services.AddAuditedCommandHandler<ResendTwoFactorChallengeCodeCommand, ResendTwoFactorChallengeCodeCommandOutput, ResendTwoFactorChallengeCodeCommandHandler>();

        // Shared by UC-11's AF-11g, UC-36's Email method and UC-46's reissue: retire-then-issue is
        // one step in one place, so the three cannot drift and leave two live codes for one
        // configuration — the retirement is what makes a code single-use in practice.
        Builder.Services.AddScoped<ITwoFactorEmailCodeIssuer, TwoFactorEmailCodeIssuer>();

        // Shared by VerifyTwoFactorAuthCommandHandler (UC-38), DisableTwoFactorAuthCommandHandler
        // (UC-39), and RegenerateRecoveryCodesCommandHandler (UC-40) — the "code against TOTP, or
        // against the current email code, or against an unused recovery code" comparison lives in
        // exactly one place.
        Builder.Services.AddScoped<ITwoFactorFactorVerifier, TwoFactorFactorVerifier>();

        // UC-39 (FR-2F-11): no validator — which shape of second factor (code vs. recoveryCode) is
        // valid depends on a database read (the pending row's AppEnabled/EmailEnabled), the same
        // reason UC-37/UC-38 have none.
        Builder.Services.AddAuditedCommandHandler<DisableTwoFactorAuthCommand, DisableTwoFactorAuthCommandOutput, DisableTwoFactorAuthCommandHandler>();

        // UC-40 (FR-2F-12): no validator either, same reason as UC-39 — reuses ITwoFactorFactorVerifier
        // (registered above) rather than reimplementing the second-factor check.
        Builder.Services.AddAuditedCommandHandler<RegenerateRecoveryCodesCommand, RegenerateRecoveryCodesCommandOutput, RegenerateRecoveryCodesCommandHandler>();

        Builder.Services.AddScoped<IScopeOwnershipChecker, ScopeOwnershipChecker>();

        // UC-11 issues tokens through the same claims mapper the middleware validates them with,
        // registered by AddTokenAuthentication in ConfigureSecurity.
        Builder.Services.AddScoped<IAuthTokenIssuer, JwtAuthTokenIssuer>();
        // Shared by LoginCommandHandler (UC-11) and VerifyTwoFactorAuthCommandHandler (UC-38) so the
        // scope-eligibility rules and the final token-issuing call live in one place.
        Builder.Services.AddScoped<PersonAuthTokenService>();
        // UC-38's challenge token (FR-2F-07…FR-2F-10, NFR-17): one class both issues and validates
        // it, registered once and exposed through both of its interfaces.
        Builder.Services.AddScoped<JwtTwoFactorChallengeTokenIssuer>();
        Builder.Services.AddScoped<ITwoFactorChallengeTokenIssuer>(
            provider => provider.GetRequiredService<JwtTwoFactorChallengeTokenIssuer>());
        Builder.Services.AddScoped<ITwoFactorChallengeTokenValidator>(
            provider => provider.GetRequiredService<JwtTwoFactorChallengeTokenIssuer>());
        Builder.Services.AddSingleton(MasterUserOptions.FromEnvironment());
        Builder.Services.AddScoped<DatabaseSeeder>();
    }

    /// <summary>
    ///     The middlewares that must run ahead of the standard pipeline. A startup filter is how they
    ///     get there: ASP.NET Core wraps the application's whole pipeline in it, so what it adds runs
    ///     before anything <c>Build</c> adds.
    /// </summary>
    private sealed class EdgePipeline(MetricsOptions metrics, IWebHostEnvironment environment) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // First, ahead of everything else — the standard pipeline's ExceptionMiddleware and
            // AuthenticationMiddleware included. The scrape endpoint is a terminal branch taken only on
            // the metrics port, so Prometheus is never asked for a token, never rate limited, never
            // subject to CORS, and the endpoint never reaches the OpenAPI document. Every request on
            // the public port falls straight through, /metrics included.
            app.UseHeimdallMetrics(metrics);

            // Local is included alongside Development because it is what a developer machine now
            // runs: the configuration loader resolves Environments/.env.<environment>, so the launch
            // profiles name Local to reach .env.local rather than relying on the loader's fallback.
            // Testing IsDevelopment() alone would have silently cost the developer exception page in
            // the one environment that exists to have it.
            if (environment.IsDevelopment() || environment.IsEnvironment("Local"))
            {
                app.UseDeveloperExceptionPage();
            }

            // Ahead of HTTPS redirection, which decides on the scheme: behind Traefik every request
            // arrives as http, and only X-Forwarded-Proto says the caller used https. The standard
            // pipeline runs the same middleware again first thing; by then the trusted hop has been
            // consumed and the connection's address is no longer a proxy's, so it changes nothing.
            app.UseForwardedHeaders();

            app.UseHttpsRedirection();

            // The health check moved under api/ so the web UI can reach it through its own host; the
            // container probe and yggdrasil's status catalog still call the root address. Rewritten
            // here, ahead of routing and authentication, so the alias runs the same endpoint with the
            // same authorization, and stays out of the OpenAPI document.
            app.UseRewriter(new RewriteOptions()
                .AddRewrite("(?i)^healthcheck(/detailed)?/?$", "api/healthcheck$1", skipRemainingRules: true));

            next(app);
        };
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
        Builder.Services.AddCors(cors => cors.AddPolicy(CorsPolicyName, policy => policy
            .WithOrigins(origins)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials()));

        Options.CorsPolicy = CorsPolicyName;
    }

    private void ConfigureSecurity()
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
        Builder.Services.AddSingleton(BuildJwtConfiguration());
        Builder.Services.AddSingleton<JwtHandler>();
        Builder.Services.AddTokenAuthentication<IdentityUserMapper>(options =>
        {
            options.Source = TokenSource.Header;
            options.EnableJwt = true;
            options.EnableGoogle = false;
            options.JwtMode = JwtValidationMode.ClaimsOnly;
        });

        AddAuthEndpointRateLimiting();

        // TH-03: bound how many Argon2id derivations run at once. Read here, with the rest of the
        // security configuration, because it belongs to the same defence: the rate limiter caps what
        // one address can ask for, and this caps what the process will attempt regardless of how
        // many addresses ask.
        PasswordHashGate.ConfigureSharedFromEnvironment();
    }

    /// <summary>
    ///     Believes <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> from the proxies
    ///     <c>HEIMDALL_TRUSTED_PROXIES</c> names, and from no one else — see
    ///     <see cref="TrustedProxyOptions" />. Applied by <c>UseForwardedHeaders</c>, which runs
    ///     ahead of everything that reads the client address: the rate limiter, and the request log.
    /// </summary>
    private void ConfigureForwardedHeaders()
    {
        var proxies = TrustedProxyOptions.FromEnvironment();

        if (proxies.InvalidEntries.Count > 0)
        {
            Log.Warning(
                "Ignoring unusable {Variable} entries {Entries}; they are neither an address nor a CIDR network",
                TrustedProxyOptions.ProxiesVariable, proxies.InvalidEntries);
        }

        if (!proxies.Configured)
        {
            Log.Warning(
                "No trusted proxy is configured ({Variable}); behind a reverse proxy every caller will " +
                "be rate limited and logged under the proxy's address",
                TrustedProxyOptions.ProxiesVariable);

            return;
        }

        Log.Information(
            "Trusting forwarded headers from {Proxies} and {Networks}",
            proxies.Proxies.Select(proxy => proxy.ToString()),
            proxies.Networks.Select(network => network.ToString()));

        Builder.Services.Configure<ForwardedHeadersOptions>(proxies.Apply);
    }

    /// <summary>
    ///     Throttles the credential-checking endpoints (login, password recovery/reset, email
    ///     verification, Google sign-in, 2FA challenge verification and resend) per calling IP
    ///     address. None of these require a bearer token, so nothing else stops a caller from firing
    ///     an unbounded burst of requests at them — each login attempt alone costs a full Argon2id
    ///     verification (600 MB / 16 threads by this codebase's hashing library default), and a 2FA
    ///     email code has only 1,000,000 possible values, so an unthrottled brute force or memory/CPU
    ///     exhaustion attempt is realistic without this. The authenticated endpoints that check a
    ///     password or a second factor (2FA confirm, disable and recovery-code regeneration, and the
    ///     erasure request) carry it too: a bearer token is not the password, and without a limit
    ///     whoever holds one could test passwords or six-digit codes against them as fast as they
    ///     could send requests, outside the login lockout. Policy name matches the
    ///     <see cref="Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute" /> applied to
    ///     each endpoint in <c>AuthController</c>.
    /// </summary>
    /// <remarks>
    ///     Partitioned by <see cref="HttpContext.Connection" />'s remote IP (an IPv6 caller by its /64 —
    ///     see <see cref="RateLimitPartitionKey" />), which is the caller's own
    ///     only when <see cref="TrustedProxyOptions" /> names the proxy in front of the API; otherwise
    ///     every caller shares the proxy's partition. Either way this is a per-instance,
    ///     defense-in-depth throttle, not a substitute for a WAF or an API gateway's own rate limiting
    ///     in front of a real deployment.
    /// </remarks>
    /// <summary>
    ///     The partition a caller's requests are counted in: the whole address for IPv4, and the
    ///     <c>/64</c> it belongs to for IPv6.
    /// </summary>
    /// <remarks>
    ///     A /64 is what one IPv6 subscriber is routinely handed, and every address inside it is
    ///     theirs to use — 2<sup>64</sup> of them. Counted per address, a single caller could give
    ///     every request a fresh partition and never meet the limit at all. An IPv4-mapped IPv6
    ///     address is one IPv4 caller, and is counted as that address.
    /// </remarks>
    public static string RateLimitPartitionKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);

        return $"{new IPAddress(bytes)}/64";
    }

    private void AddAuthEndpointRateLimiting()
    {
        Builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(AuthEndpointRateLimitPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: RateLimitPartitionKey(httpContext.Connection.RemoteIpAddress),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
        });
    }

    /// <summary>
    ///     Chooses how the two transactional emails — UC-06's verification token and UC-12's
    ///     password reset token — are delivered. With Mailgun credentials present, both go out for
    ///     real through <c>ArturRios.Messaging</c>; without them, both are logged.
    /// </summary>
    /// <remarks>
    ///     The fallback is deliberate rather than a failure: a developer running the API locally,
    ///     and the functional suite, both need person creation and password recovery to work without
    ///     credentials and without reaching the network. Failing startup instead would make Mailgun
    ///     a prerequisite for running the tests. In Production, though, the fallback logs a
    ///     verification token, a password reset token, or a 2FA code in plaintext on every send — an
    ///     account-takeover primitive for anyone who can read the logs — so an unconfigured Production
    ///     deployment fails startup outright instead of silently degrading into that.
    /// </remarks>
    private void AddEmailSenders()
    {
        var options = EmailDeliveryOptions.FromEnvironment();

        Builder.Services.AddSingleton(options);

        if (!options.MailgunConfigured)
        {
            if (Builder.Environment.IsProduction())
            {
                throw new InvalidOperationException(
                    $"Mailgun is not configured ({MailgunEmailService.ApiKeyVariable} / " +
                    $"{MailgunEmailService.DomainVariable}) in Production. Refusing to start: the " +
                    "fallback sender logs verification tokens, password reset tokens, and 2FA codes " +
                    "in plaintext, which must never happen outside local development and tests.");
            }

            Log.Warning(
                "Mailgun is not configured ({ApiKeyVariable} / {DomainVariable}); verification and " +
                "password reset tokens will be logged instead of emailed",
                MailgunEmailService.ApiKeyVariable, MailgunEmailService.DomainVariable);

            Builder.Services.AddScoped<IEmailVerificationSender, LoggingEmailVerificationSender>();
            Builder.Services.AddScoped<IPasswordResetSender, LoggingPasswordResetSender>();
            Builder.Services.AddScoped<ITwoFactorEmailSender, LoggingTwoFactorEmailSender>();
            Builder.Services.AddScoped<IRestrictionLiftNotifier, LoggingRestrictionLiftNotifier>();

            return;
        }

        // A typed client, so the Mailgun service reuses pooled connections instead of creating an
        // HttpClient per send.
        Builder.Services.AddHttpClient<IEmailService, MailgunEmailService>();
        Builder.Services.AddScoped<IEmailVerificationSender, MailgunEmailVerificationSender>();
        Builder.Services.AddScoped<IPasswordResetSender, MailgunPasswordResetSender>();
        Builder.Services.AddScoped<ITwoFactorEmailSender, MailgunTwoFactorEmailSender>();
        Builder.Services.AddScoped<IRestrictionLiftNotifier, MailgunRestrictionLiftNotifier>();
    }

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
    private void AddGoogleSignIn()
    {
        var options = GoogleSignInOptions.FromEnvironment();

        Builder.Services.AddSingleton(options);

        if (!Builder.Environment.IsProduction() && options.TestSigningConfigured)
        {
            Log.Warning(
                "Google ID tokens will be verified against a local signing secret ({Variable}), not " +
                "against Google. This is for automated tests only",
                GoogleSignInOptions.TestSigningSecretVariable);

            Builder.Services.AddScoped<IGoogleIdTokenVerifier, LocalGoogleIdTokenVerifier>();

            return;
        }

        if (!options.GoogleConfigured)
        {
            Log.Warning(
                "No Google client is configured ({Variable}); Google sign-in (UC-25) will refuse " +
                "every token",
                GoogleSignInOptions.ClientIdsVariable);

            Builder.Services.AddScoped<IGoogleIdTokenVerifier, UnconfiguredGoogleIdTokenVerifier>();

            return;
        }

        Builder.Services.AddScoped<IGoogleIdTokenVerifier, GoogleIdTokenVerifier>();
    }

    /// <summary>
    ///     Reads the token settings from the environment. The signing secret is required: with an
    ///     empty one every authenticated request dies inside the token validator with an opaque
    ///     <c>IDX10703</c>, so a missing secret fails startup instead.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="PreviousTokenSecretEnvironmentVariable" /> is how a signing secret is
    ///         replaced without signing everybody out (Threat Model TH-22). Both secrets are handed
    ///         to <c>JwtConfiguration.Keys</c>, so a token signed with either is accepted, while new
    ///         tokens are signed with the current one. Rotating is then: set the previous variable to
    ///         the secret in use, set the current variable to a new one, restart; and an hour later —
    ///         one token lifetime — clear the previous variable and restart again.
    ///     </para>
    ///     <para>
    ///         The keys carry ids because <c>JwtKey</c> requires them, but no <c>kid</c> is written to
    ///         the tokens: <c>SigningKeyId</c> is deliberately left unset, so a token names no key and
    ///         validation tries both. That is the point of not using the <c>kid</c> feature here. An
    ///         identifier stamped on the token has to stay attached to the same key forever, and these
    ///         ids are positional — today's "current" is tomorrow's "previous" — so stamping them
    ///         would make every token issued before a rotation name the wrong key afterwards, and be
    ///         refused. Deriving stable ids from the secrets instead would mean publishing something
    ///         computed from a signing key on every token, which is a poor trade for saving one HMAC
    ///         against two keys.
    ///     </para>
    /// </remarks>
    private static JwtConfiguration BuildJwtConfiguration()
    {
        var secret = Environment.GetEnvironmentVariable(TokenSecretEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                $"Environment variable '{TokenSecretEnvironmentVariable}' is unset. The API cannot " +
                "validate tokens without a signing secret.");
        }

        var expiration = double.TryParse(
            Environment.GetEnvironmentVariable(TokenExpirationEnvironmentVariable),
            out var configuredExpiration)
            ? configuredExpiration
            : DefaultTokenExpirationInSeconds;

        var previousSecret = Environment.GetEnvironmentVariable(PreviousTokenSecretEnvironmentVariable);

        // The current secret is always accepted; the previous one only while it is configured, and
        // only when it is genuinely a different key — the same value under two ids would be two ways
        // of saying nothing has been rotated yet.
        List<JwtKey> keys = [new("current", secret)];

        if (!string.IsNullOrWhiteSpace(previousSecret) && previousSecret != secret)
        {
            keys.Add(new JwtKey("previous", previousSecret));
        }

        return new JwtConfiguration(
            expiration,
            Environment.GetEnvironmentVariable(TokenIssuerEnvironmentVariable) ?? string.Empty,
            Environment.GetEnvironmentVariable(TokenAudienceEnvironmentVariable) ?? string.Empty,
            secret,
            [])
        {
            Keys = keys
        };
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

    private void ConfigureWebApi()
    {
        // The standard sequence has already called AddControllers(); calling it again adds this
        // configuration to the same MVC registration.
        //
        // MfaPendingGuardFilter (FR-2F-10, NFR-17) runs as a global MVC authorization filter, on
        // every controller action — added here rather than per-action, since a UC-38 challenge token
        // must be rejected everywhere except POST /api/auth/2fa/verify, and that endpoint needs no
        // opt-out: it never reads the challenge token as a bearer credential to begin with.
        Builder.Services.AddControllers(options =>
        {
            options.Filters.Add<MfaPendingGuardFilter>();

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
        Builder.Services.AddEndpointsApiExplorer();
    }

    /// <summary>
    ///     Registers the retention schedule (NFR-19) and the pass that enforces it.
    /// </summary>
    /// <remarks>
    ///     The options are a singleton because they are a deployment-wide policy read once at
    ///     start-up, and the purge is registered as a hosted service unless it has been switched
    ///     off — which is a deployment decision an operator has to take deliberately, since keeping
    ///     personal data past its retention period is the thing NFR-19 forbids. The handler is
    ///     registered audited like every other command: the entry recording that the schedule was
    ///     enforced is as much the point as the deletion is.
    /// </remarks>
    private void AddDataRetention()
    {
        var retention = DataRetentionOptions.FromEnvironment();

        if (retention.InvalidVariables.Count > 0)
        {
            Log.Warning(
                "Ignoring unusable retention settings and applying the documented defaults: {Variables}",
                retention.InvalidVariables);
        }

        Builder.Services.AddSingleton(retention);
        Builder.Services
            .AddAuditedCommandHandler<PurgeExpiredTokensCommand, PurgeExpiredTokensCommandOutput,
                PurgeExpiredTokensCommandHandler>();
        Builder.Services
            .AddAuditedCommandHandler<AnonymiseExpiredDeletionsCommand, AnonymiseExpiredDeletionsCommandOutput,
                AnonymiseExpiredDeletionsCommandHandler>();
        Builder.Services
            .AddAuditedCommandHandler<PseudonymiseAuditActorsCommand, PseudonymiseAuditActorsCommandOutput,
                PseudonymiseAuditActorsCommandHandler>();

        if (retention.PurgeEnabled)
        {
            Builder.Services.AddHostedService<TokenRetentionService>();
        }
        else
        {
            Log.Warning(
                "The token retention purge is switched off by {Variable}; expired tokens will not be removed",
                DataRetentionOptions.PurgeEnabledVariable);
        }

        if (retention.AnonymisationEnabled)
        {
            Builder.Services.AddHostedService<IdentityAnonymisationService>();
        }
        else
        {
            Log.Warning(
                "Identity anonymisation is switched off by {Variable}; logically deleted identities " +
                "will be kept past their retention window",
                DataRetentionOptions.AnonymisationEnabledVariable);
        }

        if (retention.SecurityMonitoringEnabled)
        {
            Builder.Services.AddHostedService<SecurityMonitoringService>();
        }
        else
        {
            Log.Warning(
                "Security signal detection is switched off by {Variable}; the audit trail and lockout " +
                "counters will keep recording what happens and nobody will be told about it",
                DataRetentionOptions.SecurityMonitoringEnabledVariable);
        }

        if (retention.AuditPseudonymisationEnabled)
        {
            Builder.Services.AddHostedService<AuditRetentionService>();
        }
        else
        {
            Log.Warning(
                "Audit attribution pseudonymisation is switched off by {Variable}; the trail will " +
                "keep naming people past its attribution period, including erased ones",
                DataRetentionOptions.AuditPseudonymisationEnabledVariable);
        }
    }

    /// <summary>
    ///     Warns when the database connection string does not require TLS (NFR-25, GDPR Art. 32,
    ///     LGPD Art. 46).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Everything this database holds is personal data, and Npgsql's default
    ///         <c>SSL Mode</c> is <c>Prefer</c> — which silently falls back to an unencrypted
    ///         connection when the server does not offer TLS. A deployment can therefore be sending
    ///         credentials and addresses in clear text across the network while looking correctly
    ///         configured, which is exactly the failure worth a start-up warning.
    ///     </para>
    ///     <para>
    ///         It warns by default rather than refusing to start, which is a deliberate departure
    ///         from how this codebase treats other security configuration — TH-23's email control
    ///         fails start-up in Production. The difference is that failing by default would take
    ///         down a working deployment on upgrade over a setting the operator may not control
    ///         directly.
    ///     </para>
    ///     <para>
    ///         Setting <c>HEIMDALL_DATA_REQUIRE_TLS=true</c> turns the warning into a refusal. It is
    ///         opt-in rather than the default for that reason, and it is the right setting for any
    ///         deployment that has confirmed TLS is in place: from that point, a connection string
    ///         that does not ask for encryption is a misconfiguration rather than a known state, and
    ///         starting anyway would be starting in the one condition the check exists to prevent.
    ///     </para>
    /// </remarks>
    private static void WarnIfDatabaseConnectionIsNotEncrypted()
    {
        var connectionString = Environment.GetEnvironmentVariable("HEIMDALL_DATA_CONNECTIONSTRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var required = string.Equals(
            Environment.GetEnvironmentVariable(RequireDatabaseTlsVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);

        var requiresTls = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Contains('='))
            .Select(part => part.Split('=', 2))
            .Where(pair => pair[0].Replace(" ", string.Empty)
                .Equals("SSLMode", StringComparison.OrdinalIgnoreCase))
            .Any(pair => pair[1].Trim() is "Require" or "VerifyCA" or "VerifyFull");

        if (requiresTls)
        {
            return;
        }

        const string explanation =
            "The database connection does not require TLS. Npgsql defaults to SSL Mode=Prefer, " +
            "which falls back to an unencrypted connection — every credential and address this " +
            "API stores would cross the network in clear text. Set SSL Mode=Require or stronger";

        if (required)
        {
            // Opted in, so the operator has said TLS is in place: a connection string that does not
            // ask for it is then a misconfiguration to fail on rather than warn about.
            throw new InvalidOperationException(
                $"{explanation}. {RequireDatabaseTlsVariable} is set, so this is refused rather than warned about.");
        }

        Log.Warning("{Explanation}", explanation);
    }

    /// <summary>
    ///     Configures the console and file sinks, and bounds how long the files live (NFR-22).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The year/month directory layout is gone, deliberately.</b> The sink used to be
    ///         wrapped in <c>WriteTo.Map</c> keyed on <c>yyyy/MM</c>, which created a <em>new sink
    ///         per month</em>. A retention limit bounds the files within one sink, so it would have
    ///         bounded each month's directory and never removed a month — the files from the first
    ///         month a deployment ran were still on disk years later. A flat rolling sink with
    ///         <c>retainedFileTimeLimit</c> is the shape where the limit actually applies.
    ///     </para>
    ///     <para>
    ///         Read straight from the environment rather than through the configured options,
    ///         because this runs before <c>LoadConfiguration</c> — the same reason the log directory
    ///         is read this way. An unusable value falls back to the documented default, silently
    ///         here because there is no logger yet to warn with; <c>AddDataRetention</c> reads the
    ///         same variable later and does warn.
    ///     </para>
    ///     <para>
    ///         <b>Every request's client IP address is logged</b>, by Util.WebApi's
    ///         <c>TraceActivityMiddleware</c>, as the address the request came from — the real caller's
    ///         once <see cref="TrustedProxyOptions" /> names the proxy in front of the API. It is
    ///         security telemetry, kept for the same period as the rest of the log, and the Data
    ///         Retention Schedule and the Privacy Notice say so.
    ///     </para>
    ///     <para>
    ///         <b><c>MailgunEmailService</c> is silenced.</b> The messaging library logs every send
    ///         with the recipient's address in the clear — "Sending e-mail to …" and "accepted the
    ///         message for …" at Information, the same on a rejection at Error — which is exactly what
    ///         NFR-22 forbids, and this codebase's own redaction cannot reach a library's templates.
    ///         Nothing is lost: <c>MailgunSender</c> already logs every outcome, refusal and exception
    ///         included, under a <c>LogSafeEmail</c> reference.
    ///     </para>
    /// </remarks>
    private static void ConfigureLogging()
    {
        var logDirectory = Environment.GetEnvironmentVariable(LogDirectoryEnvironmentVariable)
                           ?? DefaultLogDirectory;

        var retention = DataRetentionOptions.FromEnvironment().LogRetention;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Override(typeof(MailgunEmailService).FullName!, LogEventLevel.Fatal)
            .WriteTo.Console(new JsonFormatter())
            .WriteTo.File(
                new JsonFormatter(),
                Path.Combine(logDirectory, "log-.json"),
                rollingInterval: RollingInterval.Day,
                retainedFileTimeLimit: retention)
            .CreateLogger();
    }
}
