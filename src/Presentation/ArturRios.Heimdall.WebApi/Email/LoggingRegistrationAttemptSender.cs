using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Shared.Security;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Fallback <see cref="IRegistrationAttemptSender" /> for environments without Mailgun, registered
///     beside <see cref="LoggingEmailVerificationSender" />. Logs a reference to the address, never the
///     address itself (NFR-22).
/// </summary>
public class LoggingRegistrationAttemptSender(ILogger<LoggingRegistrationAttemptSender> logger)
    : IRegistrationAttemptSender
{
    public Task SendAsync(string email, bool googleAccount)
    {
        logger.LogInformation(
            "Registration attempt notice for {EmailRef} (Google account: {GoogleAccount})",
            LogSafeEmail.Reference(email), googleAccount);

        return Task.CompletedTask;
    }
}
