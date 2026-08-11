namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     Settings for UC-25's Google ID token verification. Decides, at start-up, which
///     <see cref="ArturRios.Heimdall.Command.Services.IGoogleIdTokenVerifier" /> the API runs
///     with — the same arrangement <c>EmailDeliveryOptions</c> uses to choose between real and logged
///     email delivery.
/// </summary>
public class GoogleSignInOptions
{
    /// <summary>
    ///     Comma-separated OAuth client IDs accepted as the ID token's audience (NFR-13). Google
    ///     issues one token per client, so a deployment serving several front-ends lists them all.
    /// </summary>
    public const string ClientIdsVariable = "HEIMDALL_GOOGLE_CLIENT_IDS";

    /// <summary>
    ///     Signing secret for locally minted ID tokens, used **only** by the functional test suite —
    ///     see <see cref="LocalGoogleIdTokenVerifier" /> for why it exists and what stops it reaching
    ///     production. Unset everywhere except <c>PostgresFixture</c>.
    /// </summary>
    public const string TestSigningSecretVariable = "HEIMDALL_GOOGLE_TEST_SIGNING_SECRET";

    /// <summary>The accepted audiences. Empty when the deployment has no Google client configured.</summary>
    public IReadOnlyList<string> ClientIds { get; init; } = [];

    /// <summary>The test signing secret, or empty — which is the normal case.</summary>
    public string TestSigningSecret { get; init; } = string.Empty;

    /// <summary>Whether a real Google client is configured, so tokens can be verified against Google.</summary>
    public bool GoogleConfigured => ClientIds.Count > 0;

    /// <summary>Whether the locally signed tokens of the functional suite are in play.</summary>
    public bool TestSigningConfigured => !string.IsNullOrWhiteSpace(TestSigningSecret);

    public static GoogleSignInOptions FromEnvironment() => new()
    {
        ClientIds = (Environment.GetEnvironmentVariable(ClientIdsVariable) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        TestSigningSecret =
            Environment.GetEnvironmentVariable(TestSigningSecretVariable) ?? string.Empty
    };
}
