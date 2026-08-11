using ArturRios.Heimdall.Command.Services;
using ArturRios.Messaging.Email;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Delivers UC-12's password reset token as a real email through Mailgun (FR-PR-02). Registered
///     in place of <see cref="LoggingPasswordResetSender" /> whenever Mailgun is configured.
/// </summary>
public class MailgunPasswordResetSender(
    IEmailService emailService,
    EmailDeliveryOptions options,
    ILogger<MailgunPasswordResetSender> logger)
    : MailgunSender(emailService, logger), IPasswordResetSender
{
    private const string Subject = "Reset your password";

    protected override string Purpose => "password reset";

    public Task SendAsync(string email, string token)
    {
        var link = EmailDeliveryOptions.BuildLink(options.PasswordResetUrl, token);

        // Without a configured front end there is nowhere to link to, so the token goes in plain.
        // It is still usable — UC-13 takes the token, and the link is only a convenience wrapper
        // around it.
        var body = string.IsNullOrEmpty(link)
            ? $"""
               Someone asked to reset the password for this address.

               Use this token to set a new password: {token}

               If it wasn't you, ignore this email — nothing has changed.
               """
            : $"""
               Someone asked to reset the password for this address.

               Set a new password here: {link}

               If it wasn't you, ignore this email — nothing has changed.
               """;

        return DeliverAsync(email, Subject, body);
    }
}
