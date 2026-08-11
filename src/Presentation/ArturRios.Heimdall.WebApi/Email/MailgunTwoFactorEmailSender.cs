using ArturRios.Heimdall.Command.Services;
using ArturRios.Messaging.Email;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Delivers UC-36's two-factor email code as a real email through Mailgun (FR-2F-03). Registered
///     in place of <see cref="LoggingTwoFactorEmailSender" /> whenever Mailgun is configured.
/// </summary>
public class MailgunTwoFactorEmailSender(
    IEmailService emailService,
    ILogger<MailgunTwoFactorEmailSender> logger)
    : MailgunSender(emailService, logger), ITwoFactorEmailSender
{
    private const string Subject = "Your two-factor authentication code";

    protected override string Purpose => "two-factor authentication code";

    public Task SendAsync(string email, string code)
    {
        var body = $"""
                    Your two-factor authentication code is: {code}

                    This code expires in 10 minutes. If you didn't request this, you can ignore this
                    email — nothing has changed.
                    """;

        return DeliverAsync(email, Subject, body);
    }
}
