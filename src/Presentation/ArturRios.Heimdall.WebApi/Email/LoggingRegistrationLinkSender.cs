using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Shared.Security;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Fallback <see cref="IRegistrationLinkSender" /> for environments without Mailgun credentials:
///     logs a reference to the recipient and the token instead of delivering a real email. Registered in
///     place of <see cref="MailgunRegistrationLinkSender" /> when
///     <see cref="EmailDeliveryOptions.MailgunConfigured" /> is false — which is how the functional
///     suite stays off the network.
/// </summary>
public class LoggingRegistrationLinkSender(ILogger<LoggingRegistrationLinkSender> logger)
    : IRegistrationLinkSender
{
    public Task SendAsync(string email, string token)
    {
        logger.LogInformation("Registration token issued for {EmailRef}: {Token}", LogSafeEmail.Reference(email), token);

        return Task.CompletedTask;
    }
}
