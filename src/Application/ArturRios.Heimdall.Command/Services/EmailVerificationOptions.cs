using System.Globalization;
namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Settings for email-verification token issuance. <see cref="TokenLifetime" /> is read from the
///     environment (seconds) with a 24-hour default.
/// </summary>
public class EmailVerificationOptions
{
    private const string LifetimeVariable = "HEIMDALL_EMAIL_VERIFICATION_TOKEN_EXPIRATION_IN_SECONDS";
    private const double DefaultLifetimeSeconds = 86400;

    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromSeconds(DefaultLifetimeSeconds);

    public static EmailVerificationOptions FromEnvironment()
    {
        var configuredValue = Environment.GetEnvironmentVariable(LifetimeVariable);
        var seconds = double.TryParse(configuredValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var configured)
            ? configured
            : DefaultLifetimeSeconds;

        return new EmailVerificationOptions { TokenLifetime = TimeSpan.FromSeconds(seconds) };
    }
}
