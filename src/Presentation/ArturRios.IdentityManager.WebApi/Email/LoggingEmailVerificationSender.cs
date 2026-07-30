using ArturRios.IdentityManager.Command.Services;

namespace ArturRios.IdentityManager.WebApi.Email;

/// <summary>
///     Fallback <see cref="IEmailVerificationSender" /> for environments without Mailgun
///     credentials: logs the recipient and token instead of delivering a real email. Registered in
///     place of <see cref="MailgunEmailVerificationSender" /> when
///     <see cref="EmailDeliveryOptions.MailgunConfigured" /> is false — which is how the functional
///     suite stays off the network.
/// </summary>
public class LoggingEmailVerificationSender(ILogger<LoggingEmailVerificationSender> logger)
    : IEmailVerificationSender
{
    public Task SendAsync(string email, string token)
    {
        logger.LogInformation("Email verification token issued for {Email}: {Token}", email, token);

        return Task.CompletedTask;
    }
}
