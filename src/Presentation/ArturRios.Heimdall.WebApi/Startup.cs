using ArturRios.Data.PostgreSql;
using ArturRios.Data.Relational.Core.DependencyInjection;
using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Data.Seeding;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.HealthChecks;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Heimdall.WebApi.Email;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Jwt;
using ArturRios.Messaging.Email;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Query;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Util.WebApi.Configuration;
using FluentValidation;
using ArturRios.Util.WebApi.Middleware;
using ArturRios.Util.WebApi.Security.Enums;
using ArturRios.Util.WebApi.Security.Extensions;
using ArturRios.Util.WebApi.Security.Middleware;
using Serilog;
using Serilog.Formatting.Json;

namespace ArturRios.Heimdall.WebApi;

public class Startup(string[] args) : WebApiStartup(args)
{
    private const string LogDirectoryEnvironmentVariable = "HEIMDALL_LOG_DIRECTORY";
    private const string DefaultLogDirectory = "logs";

    private const string TokenAudienceEnvironmentVariable = "HEIMDALL_AUTH_TOKEN_AUDIENCE";
    private const string TokenExpirationEnvironmentVariable = "HEIMDALL_AUTH_TOKEN_EXPIRATION_IN_SECONDS";
    private const string TokenIssuerEnvironmentVariable = "HEIMDALL_AUTH_TOKEN_ISSUER";
    private const string TokenSecretEnvironmentVariable = "HEIMDALL_AUTH_TOKEN_SECRET";
    private const double DefaultTokenExpirationInSeconds = 3600;

    public override void Build()
    {
        ConfigureLogging();

        Builder.Host.UseSerilog();

        Log.Information("Hello world!");
        Log.Information("Building web api on {EnvironmentEnvironmentName} environment", Builder.Environment.EnvironmentName);

        LoadConfiguration();

        Log.Information("Configuration loaded successfully");

        ConfigureWebApi();

        Log.Information("Web api configured successfully");

        AddDependencies();

        Log.Information("Dependencies added successfully");

        ConfigureSecurity();

        Log.Information("Security configured successfully");

        AddCustomInvalidModelStateResponse();
        UseSwaggerGen(jwtAuthentication: true);

        BuildApp();

        Log.Information("App built successfully");

        ConfigureApp();
        AddMiddlewares([typeof(ExceptionMiddleware), typeof(AuthenticationMiddleware)]);
        UseSwagger();

        Log.Information("App configured successfully");

        StartServices();

        Log.Information("Services started successfully");
        Log.Information("Ready to run!");
    }

    public override void AddDependencies()
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

        // Health checks (UC-30). Each IServiceHealthCheck is one verified dependency; the detailed
        // handler resolves them all as IEnumerable, so new checks are added by registering another.
        Builder.Services.AddScoped<IServiceHealthCheck, DatabaseHealthCheck>();
        Builder.Services
            .AddScoped<IQueryHandlerAsync<DetailedHealthQuery, HealthCheckOutput>, GetDetailedHealthQueryHandler>();

        Builder.Services.AddSingleton(EmailVerificationOptions.FromEnvironment());
        Builder.Services.AddScoped<IEmailVerificationService, EmailVerificationService>();
        Builder.Services.AddSingleton(PasswordResetOptions.FromEnvironment());
        Builder.Services.AddScoped<IPasswordResetService, PasswordResetService>();
        AddEmailSenders();
        AddGoogleSignIn();

        // UC-36 (FR-2F-02): the TOTP secret is encrypted at rest with ASP.NET Core's Data
        // Protection API before it is persisted.
        Builder.Services.AddDataProtection();
        Builder.Services.AddScoped<ITotpSecretProtector, TotpSecretProtector>();
        Builder.Services.AddScoped<IValidator<EnableTwoFactorAuthCommand>, EnableTwoFactorAuthCommandValidator>();
        Builder.Services.AddAuditedCommandHandler<EnableTwoFactorAuthCommand, EnableTwoFactorAuthCommandOutput, EnableTwoFactorAuthCommandHandler>();

        // UC-37 (FR-2F-04/05): no validator — which code(s) are required depends on the pending row's
        // AppEnabled/EmailEnabled, a database read the handler alone can make.
        Builder.Services.AddAuditedCommandHandler<ConfirmTwoFactorAuthCommand, ConfirmTwoFactorAuthCommandOutput, ConfirmTwoFactorAuthCommandHandler>();

        // UC-38 (FR-2F-06…FR-2F-09): no validator either — same reason as UC-37, plus the challenge
        // token itself is validated inside the handler, the same "opaque token as a body value"
        // shape UC-13's ResetPasswordCommand uses.
        Builder.Services.AddAuditedCommandHandler<VerifyTwoFactorAuthCommand, VerifyTwoFactorAuthCommandOutput, VerifyTwoFactorAuthCommandHandler>();

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

    public override void ConfigureApp()
    {
        App.UseCors(x => x
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader());

        App.UseHttpsRedirection();
        App.UseRouting();
        App.UseAuthentication();
        App.UseAuthorization();
        App.MapControllers();
        App.UseDeveloperExceptionPage();
    }

    public override void ConfigureSecurity()
    {
        Builder.Services.AddAuthentication("Jwt").AddJwtBearer("Jwt");
        Builder.Services.AddAuthorization();

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
    ///     a prerequisite for running the tests.
    /// </remarks>
    private void AddEmailSenders()
    {
        var options = EmailDeliveryOptions.FromEnvironment();

        Builder.Services.AddSingleton(options);

        if (!options.MailgunConfigured)
        {
            Log.Warning(
                "Mailgun is not configured ({ApiKeyVariable} / {DomainVariable}); verification and " +
                "password reset tokens will be logged instead of emailed",
                MailgunEmailService.ApiKeyVariable, MailgunEmailService.DomainVariable);

            Builder.Services.AddScoped<IEmailVerificationSender, LoggingEmailVerificationSender>();
            Builder.Services.AddScoped<IPasswordResetSender, LoggingPasswordResetSender>();
            Builder.Services.AddScoped<ITwoFactorEmailSender, LoggingTwoFactorEmailSender>();

            return;
        }

        // A typed client, so the Mailgun service reuses pooled connections instead of creating an
        // HttpClient per send.
        Builder.Services.AddHttpClient<IEmailService, MailgunEmailService>();
        Builder.Services.AddScoped<IEmailVerificationSender, MailgunEmailVerificationSender>();
        Builder.Services.AddScoped<IPasswordResetSender, MailgunPasswordResetSender>();
        Builder.Services.AddScoped<ITwoFactorEmailSender, MailgunTwoFactorEmailSender>();
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

        return new JwtConfiguration(
            expiration,
            Environment.GetEnvironmentVariable(TokenIssuerEnvironmentVariable) ?? string.Empty,
            Environment.GetEnvironmentVariable(TokenAudienceEnvironmentVariable) ?? string.Empty,
            secret,
            []);
    }

    /// <summary>
    ///     Runs the database seeder before the host starts serving, so the reference data the
    ///     application depends on is guaranteed to exist. Migrations are not applied here — the
    ///     seeder throws if any are pending.
    /// </summary>
    public override void StartServices()
    {
        using var scope = App.Services.CreateScope();

        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();

        seeder.SeedAsync().GetAwaiter().GetResult();
    }

    public override void ConfigureWebApi()
    {
        // MfaPendingGuardFilter (FR-2F-10, NFR-17) runs as a global MVC authorization filter, on
        // every controller action — added here rather than per-action, since a UC-38 challenge token
        // must be rejected everywhere except POST /api/auth/2fa/verify, and that endpoint needs no
        // opt-out: it never reads the challenge token as a bearer credential to begin with.
        Builder.Services.AddControllers(options => options.Filters.Add<MfaPendingGuardFilter>());
        Builder.Services.AddEndpointsApiExplorer();
    }

    private static void ConfigureLogging()
    {
        var logDirectory = Environment.GetEnvironmentVariable(LogDirectoryEnvironmentVariable)
                           ?? DefaultLogDirectory;

        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console(new JsonFormatter())
            .WriteTo.Map(
                keySelector: logEvent => logEvent.Timestamp.ToString("yyyy'/'MM"),
                configure: (yearMonth, sink) => sink.File(
                    new JsonFormatter(),
                    Path.Combine(logDirectory, yearMonth, "log-.json"),
                    rollingInterval: RollingInterval.Day))
            .CreateLogger();
    }
}
