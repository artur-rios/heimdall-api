using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for how start-up reads the token key material (spec, "Start-up rules"): Heimdall
// signs every token ES256, so it refuses to start without a usable signing key, naming the variable
// and never its value. An unset legacy secret is not a refusal — it only means the overlap is over,
// which TokenOverlapTests covers.
[Collection(nameof(FunctionalCollection))]
public class TokenSigningStartupTests
{
    [FunctionalTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-the-base64-of-a-pem")]
    public void GivenNoUsableSigningKey_WhenTheHostStarts_ThenStartUpIsRefusedNamingTheVariable(string? value)
    {
        // Given — the key is read once, at start-up, so this test starts its own host
        var savedEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var savedKey = Environment.GetEnvironmentVariable(TokenSigningKeys.SigningKeyVariable);
        Exception? refusal;

        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "local");
            Environment.SetEnvironmentVariable(TokenSigningKeys.SigningKeyVariable, value);

            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.UseEnvironment("Local"));

            // When
            refusal = Record.Exception(() => factory.CreateClient());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", savedEnvironment);
            Environment.SetEnvironmentVariable(TokenSigningKeys.SigningKeyVariable, savedKey);
        }

        // Then — the refusal names the variable, and the host never started
        var exception = Assert.IsType<InvalidOperationException>(Innermost(refusal));
        Assert.Contains($"'{TokenSigningKeys.SigningKeyVariable}'", exception.Message);
        Assert.Contains("Refusing to start", exception.Message);
    }

    private static Exception? Innermost(Exception? exception)
    {
        while (exception?.InnerException is not null)
        {
            exception = exception.InnerException;
        }

        return exception;
    }
}
