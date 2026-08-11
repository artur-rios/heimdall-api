using ArturRios.Heimdall.Command.Services;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Fallback <see cref="ITwoFactorEmailSender" /> for environments without Mailgun credentials:
///     logs the recipient and code instead of delivering a real email. Registered in place of
///     <see cref="MailgunTwoFactorEmailSender" /> when
///     <see cref="EmailDeliveryOptions.MailgunConfigured" /> is false.
/// </summary>
public class LoggingTwoFactorEmailSender(ILogger<LoggingTwoFactorEmailSender> logger)
    : ITwoFactorEmailSender
{
    public Task SendAsync(string email, string code)
    {
        logger.LogInformation("Two-factor email code issued for {Email}: {Code}", email, code);

        return Task.CompletedTask;
    }
}
