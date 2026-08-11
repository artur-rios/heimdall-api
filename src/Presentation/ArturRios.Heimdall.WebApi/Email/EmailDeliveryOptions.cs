using ArturRios.Messaging.Email;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Settings for outbound transactional email. Two halves: whether Mailgun is configured at all,
///     which decides between real delivery and the logging senders at start-up, and the front-end
///     links the two emails point at.
/// </summary>
/// <remarks>
///     The Mailgun credentials themselves are read by <see cref="MailgunEmailService" /> from its
///     own environment variables at call time — this class only observes whether they are present,
///     so an unconfigured environment degrades to logging instead of failing every send.
/// </remarks>
public class EmailDeliveryOptions
{
    private const string VerificationUrlVariable = "HEIMDALL_EMAIL_VERIFICATION_URL";
    private const string PasswordResetUrlVariable = "HEIMDALL_PASSWORD_RESET_URL";

    /// <summary>Base link a verification token is appended to (UC-14).</summary>
    public string VerificationUrl { get; init; } = string.Empty;

    /// <summary>Base link a password reset token is appended to (UC-13).</summary>
    public string PasswordResetUrl { get; init; } = string.Empty;

    /// <summary>
    ///     Whether Mailgun has both the credentials it needs. False in any environment that has not
    ///     been given them — notably the functional test suite, which must never reach the network.
    /// </summary>
    public bool MailgunConfigured { get; init; }

    public static EmailDeliveryOptions FromEnvironment() => new()
    {
        VerificationUrl = Environment.GetEnvironmentVariable(VerificationUrlVariable) ?? string.Empty,
        PasswordResetUrl = Environment.GetEnvironmentVariable(PasswordResetUrlVariable) ?? string.Empty,
        MailgunConfigured =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MailgunEmailService.ApiKeyVariable)) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MailgunEmailService.DomainVariable))
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
