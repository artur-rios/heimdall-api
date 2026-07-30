using ArturRios.IdentityManager.Command.Services;

namespace ArturRios.IdentityManager.WebApi.Email;

/// <summary>
///     Fallback <see cref="IPasswordResetSender" /> for environments without Mailgun credentials:
///     logs the recipient and token instead of delivering a real email. Registered in place of
///     <see cref="MailgunPasswordResetSender" /> when
///     <see cref="EmailDeliveryOptions.MailgunConfigured" /> is false.
/// </summary>
public class LoggingPasswordResetSender(ILogger<LoggingPasswordResetSender> logger)
    : IPasswordResetSender
{
    public Task SendAsync(string email, string token)
    {
        logger.LogInformation("Password reset token issued for {Email}: {Token}", email, token);

        return Task.CompletedTask;
    }
}
