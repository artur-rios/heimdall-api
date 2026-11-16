using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.Test.Attributes;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.WebApi.Tests;

// The verifier a deployment with no Google client runs with (UC-25 AF-25a): it has no audience to check a token
// against, so it refuses every one, and says why, rather than trusting a token no one checked.
public class UnconfiguredGoogleIdTokenVerifierTests
{
    private sealed class RecordingLogger : ILogger<UnconfiguredGoogleIdTokenVerifier>
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl")]
    public async Task GivenAnyToken_WhenVerified_ThenItIsRefusedAndTheMissingClientIsLogged(string idToken)
    {
        // Given
        var logger = new RecordingLogger();
        var verifier = new UnconfiguredGoogleIdTokenVerifier(logger);

        // When
        var payload = await verifier.VerifyAsync(idToken);

        // Then nothing is accepted, and the warning names the variable to set, never the token
        Assert.Null(payload);
        var (level, text) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains(GoogleSignInOptions.ClientIdsVariable, text);
        if (idToken.Length > 0)
        {
            Assert.DoesNotContain(idToken, text);
        }
    }
}
