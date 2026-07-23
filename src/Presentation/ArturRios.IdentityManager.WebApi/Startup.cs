using ArturRios.Data.PostgreSql;
using ArturRios.Data.Relational.Core.DependencyInjection;
using ArturRios.IdentityManager.Command.Handlers;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Input.Validation;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Data.Configuration;
using ArturRios.IdentityManager.Query.Handlers;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Mediator.Query;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Util.WebApi.Configuration;
using FluentValidation;
using ArturRios.Util.WebApi.Middleware;
using ArturRios.Util.WebApi.Security.Middleware;
using Serilog;
using Serilog.Formatting.Json;

namespace ArturRios.IdentityManager.WebApi;

public class Startup(string[] args) : WebApiStartup(args)
{
    private const string LogDirectoryEnvironmentVariable = "IDENTITY_MANAGER_LOG_DIRECTORY";
    private const string DefaultLogDirectory = "logs";

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
        Builder.Services.AddPostgreSqlProvider();
        Builder.Services.AddDataConfigFromEnvironment<AppDbContext>("IDENTITY_MANAGER_DATA");

        Builder.Services.AddScoped<CommandMediator>();
        Builder.Services.AddScoped<IValidator<CreateScopeCommand>, CreateScopeCommandValidator>();
        Builder.Services
            .AddScoped<ICommandHandlerAsync<CreateScopeCommand, CreateScopeCommandOutput>, CreateScopeCommandHandler>();

        Builder.Services.AddScoped<QueryMediator>();
        Builder.Services
            .AddScoped<IQueryHandlerAsync<GetScopeByIdQuery, ScopeOutput>, GetScopeByIdQueryHandler>();
        Builder.Services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopesQuery, ScopeOutput>, ListScopesQueryHandler>();
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
