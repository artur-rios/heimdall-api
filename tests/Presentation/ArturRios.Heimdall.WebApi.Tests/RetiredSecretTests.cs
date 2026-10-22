using System.Net;
using System.Text.Json;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Serilog;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for the retired shared secret (Threat Model TH-43). Heimdall 4.0.0 no longer reads
// HEIMDALL_AUTH_TOKEN_SECRET or its _PREVIOUS: an HS256 token signed with either is refused like any
// other forgery, and a host that still has them set starts, serves its key set, and warns once per
// variable so the operator removes it. The fixture keeps setting both, so the whole suite runs against
// a host they are set on.
[Collection(nameof(FunctionalCollection))]
public class RetiredSecretTests() : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string SecretVariable = "HEIMDALL_AUTH_TOKEN_SECRET";
    private const string PreviousSecretVariable = "HEIMDALL_AUTH_TOKEN_SECRET_PREVIOUS";
    private const string LogDirectoryVariable = "HEIMDALL_LOG_DIRECTORY";
    private const string RetiredTemplate = "{Variable} is set but no longer read; remove it";

    private static readonly Guid SystemAdmin = PostgresFixture.StandInPersonIds[Roles.SystemAdmin];

    private Task<HttpOutput<DataOutput<PersonOutput?>?>> ReadSelfAsync() =>
        Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{SystemAdmin}");

    [FunctionalTheory]
    [InlineData(PostgresFixture.TokenSecret)]
    [InlineData(PostgresFixture.PreviousTokenSecret)]
    public async Task GivenTheRetiredSecretIsStillSet_WhenALegacyHs256TokenIsUsed_ThenItIs401(string secret)
    {
        // Given — the token a caller held under 2.x, signed with the secret, or the previous one, the
        // host still has set: before the host started, no kid, the person's current stamp
        Authorize(TestTokens.Legacy(SystemAdmin, (int)Roles.SystemAdmin, secret));

        // When
        var response = await ReadSelfAsync();

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenTheRetiredSecretIsStillSet_WhenTheHostStarts_ThenTheKeySetIsServed()
    {
        // Given — the fixture's host, started with both retired variables set

        // When
        var response = await Gateway.Client.GetAsync("/.well-known/jwks.json");

        // Then — the ES256 keys alone, and an ES256 token still works
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.All(
            document.RootElement.GetProperty("keys").EnumerateArray(),
            key => Assert.Equal("ES256", key.GetProperty("alg").GetString()));

        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        Assert.Equal(HttpStatusCode.OK, (await ReadSelfAsync()).StatusCode);
    }

    [FunctionalTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task GivenRetiredVariables_WhenTheHostStarts_ThenItWarnsOncePerSetVariable(
        bool secretSet, bool previousSet)
    {
        // Given — a host of its own, since the variables are looked at once, at start-up, logging to a
        // directory of its own so its start-up lines can be read back. The values are ones 3.x refused
        // to start with — too short, and _PREVIOUS without the secret — so a host that read them would
        // not start at all.
        const string secretValue = "retired-short-1";
        const string previousValue = "retired-short-2";
        var logDirectory = Directory.CreateTempSubdirectory("heimdall-retired-secret-").FullName;
        var savedLogger = Log.Logger;
        var saved = new[] { "ASPNETCORE_ENVIRONMENT", SecretVariable, PreviousSecretVariable, LogDirectoryVariable }
            .ToDictionary(variable => variable, Environment.GetEnvironmentVariable);
        HttpStatusCode keySet;

        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "local");
            Environment.SetEnvironmentVariable(SecretVariable, secretSet ? secretValue : null);
            Environment.SetEnvironmentVariable(PreviousSecretVariable, previousSet ? previousValue : null);
            Environment.SetEnvironmentVariable(LogDirectoryVariable, logDirectory);

            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.UseEnvironment("Local"));

            using var client = factory.CreateClient();

            // When
            using var response = await client.GetAsync("/.well-known/jwks.json");
            keySet = response.StatusCode;
        }
        finally
        {
            // The host replaced the process-wide logger with one writing to the directory above:
            // close it, so its file is complete, and put the suite's back.
            await Log.CloseAndFlushAsync();
            Log.Logger = savedLogger;

            foreach (var (variable, value) in saved)
            {
                Environment.SetEnvironmentVariable(variable, value);
            }
        }

        string[] lines;

        try
        {
            lines = [.. Directory.EnumerateFiles(logDirectory).SelectMany(File.ReadAllLines)];
        }
        finally
        {
            Directory.Delete(logDirectory, recursive: true);
        }

        // Then — the host started and serves its key set
        Assert.Equal(HttpStatusCode.OK, keySet);

        // one Warning per variable that is set, naming it, and neither value anywhere
        string[] expected =
        [
            .. new[] { (secretSet, SecretVariable), (previousSet, PreviousSecretVariable) }
                .Where(variable => variable.Item1)
                .Select(variable => variable.Item2)
        ];
        var entries = lines.Select(line => JsonDocument.Parse(line).RootElement).ToList();
        var warnings = entries
            .Where(entry => entry.GetProperty("MessageTemplate").GetString() == RetiredTemplate)
            .ToList();

        Assert.All(warnings, entry => Assert.Equal("Warning", entry.GetProperty("Level").GetString()));
        Assert.Equal(
            expected,
            warnings.Select(entry => entry.GetProperty("Properties").GetProperty("Variable").GetString()));
        Assert.DoesNotContain(lines, line => line.Contains(secretValue) || line.Contains(previousValue));

        // and the start-up line says HS256 tokens are refused
        var tokens = Assert.Single(
            entries, entry => entry.GetProperty("MessageTemplate").GetString()!.StartsWith("Tokens: signed ES256"));
        Assert.Equal("refused", tokens.GetProperty("Properties").GetProperty("LegacyState").GetString());
    }
}

// Which retired variables start-up warns about, decided apart from the host: a value of nothing but
// blanks is unset, as 3.x read it, and an env file's literal empty value reaches the process as one.
// The functional theory above cannot present an empty value, because setting a process variable to
// the empty string removes it.
public class RetiredSecretValueTests
{
    private const string SecretVariable = "HEIMDALL_AUTH_TOKEN_SECRET";
    private const string PreviousSecretVariable = "HEIMDALL_AUTH_TOKEN_SECRET_PREVIOUS";

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" \t\r\n ")]
    public void GivenARetiredVariableUnsetEmptyOrBlank_WhenStartUpLooks_ThenItIsNotSet(string? value)
    {
        // Given / When — both variables hold the same value
        var set = TokenSecurityRegistrationExtensions.SetRetiredTokenSecretVariables(_ => value);

        // Then
        Assert.Empty(set);
    }

    [UnitFact]
    public void GivenARetiredVariableHoldingAValue_WhenStartUpLooks_ThenItIsNamed()
    {
        // Given — only the previous secret holds anything, surrounded by blanks
        var values = new Dictionary<string, string?> { [SecretVariable] = " ", [PreviousSecretVariable] = " x " };

        // When
        var set = TokenSecurityRegistrationExtensions.SetRetiredTokenSecretVariables(variable => values[variable]);

        // Then
        Assert.Equal([PreviousSecretVariable], set);
    }
}
