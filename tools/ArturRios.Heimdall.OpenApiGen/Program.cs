using ArturRios.Heimdall.WebApi.Binding;
using ArturRios.Heimdall.WebApi.Controllers;
using ArturRios.Heimdall.WebApi.Documentation;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;

namespace ArturRios.Heimdall.OpenApiGen;

/// <summary>
///     Writes the OpenAPI document the documentation site publishes at /openapi/heimdall.json.
///
///     The document is produced by reflecting over the Web API's controllers — the same ApiExplorer
///     metadata a running instance exposes — without starting the Web API itself. Nothing here
///     connects to a database, reads an environment file, or needs a port: the API's own
///     <c>Startup</c> is never used, only its controller assembly and the two pieces of API
///     configuration that shape the document: <see cref="SwaggerConfiguration" />, which drives the
///     document's shape, and <see cref="ModelBindingConfiguration" />, which decides which properties
///     are bindable in the first place. That is what makes this runnable in CI and on a clean
///     checkout, where <c>dotnet swagger tofile</c> — which boots the real host, and so needs a
///     migrated database to get past the seeder — is not.
///
///     Because it applies the very same <see cref="SwaggerConfiguration.Configure" /> and
///     <see cref="ModelBindingConfiguration.Configure" /> the running API applies, the published
///     document and the one at /swagger/v1/swagger.json are the same document.
///
///     Run it through scripts/openapi.py rather than directly, so the output always lands where the
///     site expects it.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var outputPath = args.Length > 0
            ? args[0]
            : Path.Combine("docs", "openapi", "heimdall.json");

        var builder = WebApplication.CreateBuilder();

        // The controllers live in the Web API assembly, not this one, so ApiExplorer has to be
        // pointed at it explicitly. Controllers are only reflected over, never constructed, so none
        // of their dependencies (the mediators, the DbContext) need to be registered.
        //
        // ModelBindingConfiguration.Configure is also applied by Startup.ConfigureWebApi for the
        // running API. This generator builds its own AddControllers() and never runs Startup, so the
        // two call sites are independent — drop this one and the published document regains the
        // server-populated query parameters Startup still keeps unbindable at runtime (caught by
        // OpenApiContractTests and scripts/openapi.py --check, unlike dropping the Startup site).
        builder.Services
            .AddControllers(ModelBindingConfiguration.Configure)
            .AddApplicationPart(typeof(AuthController).Assembly);

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(SwaggerConfiguration.Configure);

        var app = builder.Build();

        app.MapControllers();

        var provider = app.Services.GetRequiredService<IAsyncSwaggerProvider>();
        var document = await provider.GetSwaggerAsync(SwaggerConfiguration.DocumentName);

        var fullPath = Path.GetFullPath(outputPath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using (var stream = File.Create(fullPath))
        {
            await document.SerializeAsJsonAsync(stream, OpenApiSpecVersion.OpenApi3_0);
        }

        var operations = document.Paths.Sum(path => path.Value.Operations?.Count ?? 0);

        Console.WriteLine($"Wrote {operations} operations across {document.Paths.Count} paths to {fullPath}");

        return 0;
    }
}
