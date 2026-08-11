namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Settings for password reset token issuance (UC-12, FR-PR-02). <see cref="TokenLifetime" /> is
///     read from the environment (seconds) with a one-hour default — far shorter than the
///     verification token's day, since this one is what stands between an intercepted mailbox and a
///     changed password.
/// </summary>
public class PasswordResetOptions
{
    private const string LifetimeVariable = "HEIMDALL_PASSWORD_RESET_TOKEN_EXPIRATION_IN_SECONDS";
    private const double DefaultLifetimeSeconds = 3600;

    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromSeconds(DefaultLifetimeSeconds);

    public static PasswordResetOptions FromEnvironment()
    {
        var seconds = double.TryParse(Environment.GetEnvironmentVariable(LifetimeVariable), out var configured)
            ? configured
            : DefaultLifetimeSeconds;

        return new PasswordResetOptions { TokenLifetime = TimeSpan.FromSeconds(seconds) };
    }
}
