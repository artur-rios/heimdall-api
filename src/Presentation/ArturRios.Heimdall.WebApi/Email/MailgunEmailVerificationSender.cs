using ArturRios.Heimdall.Command.Services;
using ArturRios.Messaging.Email;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Delivers UC-06's verification token as a real email through Mailgun (FR-EV-01/02), replacing
///     the <see cref="LoggingEmailVerificationSender" /> stub that use case shipped with. Registered
///     whenever Mailgun is configured.
/// </summary>
public class MailgunEmailVerificationSender(
    IEmailService emailService,
    EmailDeliveryOptions options,
    ILogger<MailgunEmailVerificationSender> logger)
    : MailgunSender(emailService, logger), IEmailVerificationSender
{
    private const string Subject = "Verify your email address";

    protected override string Purpose => "email verification";

    public Task SendAsync(string email, string token)
    {
        var link = EmailDeliveryOptions.BuildLink(options.VerificationUrl, token);

        var body = string.IsNullOrEmpty(link)
            ? $"""
               Welcome! Confirm this address to finish setting up your account.

               Use this token to verify it: {token}
               """
            : $"""
               Welcome! Confirm this address to finish setting up your account.

               Verify it here: {link}
               """;

        return DeliverAsync(email, Subject, body);
    }
}
