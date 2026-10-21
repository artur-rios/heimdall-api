using System.Security.Cryptography;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for how start-up reads the token key material (spec, "Start-up rules"): Heimdall
// signs every token ES256, so it refuses to start without a usable signing key, naming the variable
// and never its value. The retired shared secret is no refusal either way: RetiredSecretTests covers
// a host that still has it set.
[Collection(nameof(FunctionalCollection))]
public class TokenSigningStartupTests
{
    [FunctionalTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-the-base64-of-a-pem")]
    public void GivenNoUsableSigningKey_WhenTheHostStarts_ThenStartUpIsRefusedNamingTheVariable(string? value)
    {
        // Given / When — the key is read once, at start-up, so each case starts its own host
        var refusal = StartUpRefusal(value);

        // Then — the refusal names the variable, and the host never started
        var exception = Assert.IsType<InvalidOperationException>(refusal);
        Assert.Contains($"'{TokenSigningKeys.SigningKeyVariable}'", exception.Message);
        Assert.Contains("Refusing to start", exception.Message);
    }

    [FunctionalTheory]
    [InlineData("not base64")]
    [InlineData("the base64 of no pem")]
    [InlineData("the base64 of a p-384 key")]
    [InlineData("the pem itself")]
    public void GivenAnUnusableSigningKey_WhenTheHostStarts_ThenTheRefusalNeverCarriesTheValue(string kind)
    {
        // Given — a value for each refusal that reads it, the keys generated here and thrown away
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var value = kind switch
        {
            "not base64" => "not-the-base64-of-a-pem",
            "the base64 of no pem" => UnitSigningKeys.EnvValueOf("not a pem"),
            "the base64 of a p-384 key" => UnitSigningKeys.EnvValueOf(p384.ExportPkcs8PrivateKeyPem()),
            "the pem itself" => p256.ExportPkcs8PrivateKeyPem(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        // When
        var refusal = StartUpRefusal(value);

        // Then — refused, and no part of the message is the value
        var exception = Assert.IsType<InvalidOperationException>(refusal);
        Assert.Contains("Refusing to start", exception.Message);
        Assert.DoesNotContain(value, exception.Message);
    }

    // Starts a host with HEIMDALL_AUTH_SIGNING_KEY set to value, and returns the innermost exception
    // creating its client threw, if any. The environment is put back either way.
    private static Exception? StartUpRefusal(string? value)
    {
        var savedEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var savedKey = Environment.GetEnvironmentVariable(TokenSigningKeys.SigningKeyVariable);

        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "local");
            Environment.SetEnvironmentVariable(TokenSigningKeys.SigningKeyVariable, value);

            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.UseEnvironment("Local"));

            return Innermost(Record.Exception(() => factory.CreateClient()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", savedEnvironment);
            Environment.SetEnvironmentVariable(TokenSigningKeys.SigningKeyVariable, savedKey);
        }
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
