using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ArturRios.Heimdall.Data.Configuration;

namespace ArturRios.Heimdall.WebApi.Tests;

// Verification for NFR-06 (horizontal scaling) where it actually bites: the Data Protection key ring
// that UC-36's TOTP secrets are encrypted with (NFR-16).
//
// Data Protection's default key store is a directory on the local filesystem. The image does not
// persist it and two containers do not share it, so a secret encrypted by one instance could not be
// read by another, or by the same instance after it was recreated. Nothing announced that: the
// protector throws CryptographicException, ITotpCodeVerifier catches it and reports a wrong code, and
// every caller whose second factor is an authenticator app is quietly locked out of it.
//
// The key ring is now in the database — shared state the deployment already has. These tests build
// two providers the way two instances would, with nothing in common but that database.
[Collection(nameof(FunctionalCollection))]
public class DataProtectionKeyRingTests(PostgresFixture db)
{
    /// <summary>
    ///     A protector configured exactly as <c>Startup</c> configures the running API's, with its own
    ///     service provider and its own <see cref="AppDbContext" /> — the isolation two instances have.
    /// </summary>
    private ITotpSecretProtector BuildInstanceProtector()
    {
        var services = new ServiceCollection();

        // AppDbContext takes an ILoggerFactory; the running host has one from Serilog.
        services.AddLogging();

        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(db.ConnectionString)
            .UseLoggerFactory(NullLoggerFactory.Instance)
            .UseSnakeCaseNamingConvention());

        services.AddSingleton(DbContextDiagnosticsOptions.Disabled);
        services.AddDataProtection()
            .PersistKeysToDbContext<AppDbContext>()
            .SetApplicationName("Heimdall");

        return new TotpSecretProtector(
            services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>());
    }

    [FunctionalFact]
    public void GivenASecretProtectedByOneInstance_WhenAnotherInstanceReadsIt_ThenItIsRecovered()
    {
        // Given a TOTP secret encrypted by one instance
        const string secret = "JBSWY3DPEHPK3PXP";

        var protectedSecret = BuildInstanceProtector().Protect(secret);

        // When a second instance — separate container, separate service provider, sharing only the
        // database — unprotects it
        var recovered = BuildInstanceProtector().Unprotect(protectedSecret);

        // Then it reads back exactly. Against the default key store this threw
        // CryptographicException, and the caller was told their authenticator code was wrong.
        Assert.Equal(secret, recovered);
    }

    [FunctionalFact]
    public async Task GivenTheApiHasStarted_WhenTheKeyRingIsInspected_ThenItIsInTheDatabase()
    {
        // The key ring has to be somewhere durable for the test above to mean anything beyond one
        // process: a shared in-memory ring would satisfy it and still lose every secret on restart.
        BuildInstanceProtector().Protect("JBSWY3DPEHPK3PXP");

        await using var context = db.CreateContext();

        Assert.NotEmpty(await context.DataProtectionKeys.ToListAsync());
    }
}
