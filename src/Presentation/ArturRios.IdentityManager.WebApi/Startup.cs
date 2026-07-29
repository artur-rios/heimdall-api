using ArturRios.Data.PostgreSql;
using ArturRios.Data.Relational.Core.DependencyInjection;
using ArturRios.IdentityManager.Command.Handlers;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Input.Validation;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Command.Services;
using ArturRios.IdentityManager.Data.Configuration;
using ArturRios.IdentityManager.Data.Seeding;
using ArturRios.IdentityManager.Query.Handlers;
using ArturRios.IdentityManager.Query.HealthChecks;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.WebApi.Security;
using ArturRios.Jwt;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Command.Interfaces;
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

namespace ArturRios.IdentityManager.WebApi;

public class Startup(string[] args) : WebApiStartup(args)
{
    private const string LogDirectoryEnvironmentVariable = "IDENTITY_MANAGER_LOG_DIRECTORY";
    private const string DefaultLogDirectory = "logs";

    private const string TokenAudienceEnvironmentVariable = "IDENTITY_MANAGER_AUTH_TOKEN_AUDIENCE";
    private const string TokenExpirationEnvironmentVariable = "IDENTITY_MANAGER_AUTH_TOKEN_EXPIRATION_IN_SECONDS";
    private const string TokenIssuerEnvironmentVariable = "IDENTITY_MANAGER_AUTH_TOKEN_ISSUER";
    private const string TokenSecretEnvironmentVariable = "IDENTITY_MANAGER_AUTH_TOKEN_SECRET";
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
        Builder.Services.AddDataConfigFromEnvironment<AppDbContext>("IDENTITY_MANAGER_DATA");

        Builder.Services.AddScoped<CommandMediator>();
        Builder.Services.AddScoped<IValidator<CreateScopeCommand>, CreateScopeCommandValidator>();
        Builder.Services
            .AddScoped<ICommandHandlerAsync<CreateScopeCommand, CreateScopeCommandOutput>, CreateScopeCommandHandler>();
        Builder.Services.AddScoped<IValidator<UpdateScopeCommand>, UpdateScopeCommandValidator>();
        Builder.Services
            .AddScoped<ICommandHandlerAsync<UpdateScopeCommand, UpdateScopeCommandOutput>, UpdateScopeCommandHandler>();
        Builder.Services
            .AddScoped<ICommandHandlerAsync<DeleteScopeCommand, DeleteScopeCommandOutput>, DeleteScopeCommandHandler>();
        Builder.Services
            .AddScoped<ICommandHandlerAsync<HardDeleteScopeCommand, HardDeleteScopeCommandOutput>, HardDeleteScopeCommandHandler>();
        Builder.Services.AddScoped<IValidator<CreateAdminCommand>, CreateAdminCommandValidator>();
        Builder.Services
            .AddScoped<ICommandHandlerAsync<CreateAdminCommand, CreatePersonCommandOutput>, CreateAdminCommandHandler>();
        Builder.Services.AddScoped<IValidator<CreateUserCommand>, CreateUserCommandValidator>();
        Builder.Services
            .AddScoped<ICommandHandlerAsync<CreateUserCommand, CreatePersonCommandOutput>, CreateUserCommandHandler>();
        Builder.Services.AddScoped<IValidator<CreateScopeOwnerCommand>, CreateScopeOwnerCommandValidator>();
        Builder.Services
            .AddScoped<ICommandHandlerAsync<CreateScopeOwnerCommand, CreatePersonCommandOutput>, CreateScopeOwnerCommandHandler>();

        Builder.Services.AddScoped<QueryMediator>();
        Builder.Services
            .AddScoped<IQueryHandlerAsync<GetScopeByIdQuery, ScopeOutput>, GetScopeByIdQueryHandler>();
        Builder.Services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopesQuery, ScopeOutput>, ListScopesQueryHandler>();

        // Health checks (UC-30). Each IServiceHealthCheck is one verified dependency; the detailed
        // handler resolves them all as IEnumerable, so new checks are added by registering another.
        Builder.Services.AddScoped<IServiceHealthCheck, DatabaseHealthCheck>();
        Builder.Services
            .AddScoped<IQueryHandlerAsync<DetailedHealthQuery, HealthCheckOutput>, GetDetailedHealthQueryHandler>();

        Builder.Services.AddSingleton(EmailVerificationOptions.FromEnvironment());
        Builder.Services.AddScoped<IEmailVerificationSender, LoggingEmailVerificationSender>();
        Builder.Services.AddScoped<IEmailVerificationService, EmailVerificationService>();
        Builder.Services.AddScoped<IScopeOwnershipChecker, ScopeOwnershipChecker>();
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
        // container, and JwtTokenValidator additionally needs JwtConfiguration and JwtHandler.
        // Defaults are kept: app JWT only, read from the Authorization header, user rebuilt from
        // claims, so no IAuthenticationProvider is required.
        Builder.Services.AddSingleton(BuildJwtConfiguration());
        Builder.Services.AddSingleton<JwtHandler>();
        Builder.Services.AddTokenAuthentication(options =>
        {
            options.Source = TokenSource.Header;
            options.EnableJwt = true;
            options.EnableGoogle = false;
            options.JwtMode = JwtValidationMode.ClaimsOnly;
        });
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
        Builder.Services.AddControllers();
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
