namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Settings for outbound transactional email. Two halves: where Hermes is and the key to call it with,
///     which decides between real delivery and the logging senders at start-up, and the front-end
///     links the two emails point at.
/// </summary>
/// <remarks>
///     An environment without both Hermes settings degrades to logging instead of failing every send. Heimdall
///     holds no Mailgun credentials: Hermes does.
/// </remarks>
public class EmailDeliveryOptions
{
    private const string VerificationUrlVariable = "HEIMDALL_EMAIL_VERIFICATION_URL";
    private const string PasswordResetUrlVariable = "HEIMDALL_PASSWORD_RESET_URL";

    public const string HermesBaseUrlVariable = "HERMES_BASE_URL";
    public const string HermesApiKeyVariable = "HERMES_API_KEY";

    /// <summary>Base link a verification token is appended to (UC-14).</summary>
    public string VerificationUrl { get; init; } = string.Empty;

    /// <summary>Base link a password reset token is appended to (UC-13).</summary>
    public string PasswordResetUrl { get; init; } = string.Empty;

    /// <summary>Hermes at its internal alias, e.g. <c>http://hermes-api.production:8080</c>.</summary>
    public string HermesBaseUrl { get; init; } = string.Empty;

    /// <summary>The key Heimdall authenticates to Hermes with.</summary>
    public string HermesApiKey { get; init; } = string.Empty;

    /// <summary>
    ///     Whether both Hermes settings are present. False in any environment that has not been given them —
    ///     notably the functional test suite, which must never reach the network.
    /// </summary>
    public bool HermesConfigured => !string.IsNullOrWhiteSpace(HermesBaseUrl) && !string.IsNullOrWhiteSpace(HermesApiKey);

    public static EmailDeliveryOptions FromEnvironment() => new()
    {
        VerificationUrl = Environment.GetEnvironmentVariable(VerificationUrlVariable) ?? string.Empty,
        PasswordResetUrl = Environment.GetEnvironmentVariable(PasswordResetUrlVariable) ?? string.Empty,
        HermesBaseUrl = Environment.GetEnvironmentVariable(HermesBaseUrlVariable) ?? string.Empty,
        HermesApiKey = Environment.GetEnvironmentVariable(HermesApiKeyVariable) ?? string.Empty
    };

    /// <summary>
    ///     Appends a token to a configured base link as a <c>token</c> query parameter, respecting a
    ///     query string the base may already carry. Returns an empty string when no link is
    ///     configured, which is the signal for the senders to write the bare token instead.
    /// </summary>
    public static string BuildLink(string baseUrl, string token)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        var separator = baseUrl.Contains('?') ? '&' : '?';

        return $"{baseUrl}{separator}token={Uri.EscapeDataString(token)}";
    }
}
