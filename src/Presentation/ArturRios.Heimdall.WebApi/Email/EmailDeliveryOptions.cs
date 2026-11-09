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
    ///     Whether Hermes can be called: an absolute http or https address, and a key. False in any environment that
    ///     has not been given them — notably the functional test suite, which must never reach the network. An
    ///     address that is not one would otherwise pass the Production check and fail every send.
    /// </summary>
    public bool HermesConfigured =>
        !string.IsNullOrWhiteSpace(HermesApiKey)
        && Uri.TryCreate(HermesBaseUrl, UriKind.Absolute, out var address)
        && address.Scheme is "http" or "https";

    public static EmailDeliveryOptions FromEnvironment() => From(Environment.GetEnvironmentVariable);

    public static EmailDeliveryOptions From(Func<string, string?> read) => new()
    {
        VerificationUrl = read(VerificationUrlVariable) ?? string.Empty,
        PasswordResetUrl = read(PasswordResetUrlVariable) ?? string.Empty,
        HermesBaseUrl = read(HermesBaseUrlVariable) ?? string.Empty,
        HermesApiKey = read(HermesApiKeyVariable) ?? string.Empty
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
