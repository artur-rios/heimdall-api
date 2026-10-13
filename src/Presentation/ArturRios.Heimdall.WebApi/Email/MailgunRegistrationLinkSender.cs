using ArturRios.Heimdall.Command.Services;
using ArturRios.Messaging.Email;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Delivers UC-47's registration link through Mailgun. It tells the registrant that the link alone
///     finishes nothing: the password chosen at sign-up must accompany it. A failure is logged by
///     <see cref="MailgunSender" /> and never propagated.
/// </summary>
public class MailgunRegistrationLinkSender(
    IEmailService emailService,
    EmailDeliveryOptions options,
    ILogger<MailgunRegistrationLinkSender> logger)
    : MailgunSender(emailService, logger), IRegistrationLinkSender
{
    private const string Subject = "Finish creating your account";

    protected override string Purpose => "registration link";

    public Task SendAsync(string email, string token)
    {
        var link = EmailDeliveryOptions.BuildLink(options.VerificationUrl, token);

        var body = string.IsNullOrEmpty(link)
            ? $"""
               Someone asked to create an account with this address.

               Use this token, with the password you chose when you signed up: {token}

               If you did not ask for this, ignore this email. No account is created unless the link is used with that password.
               """
            : $"""
               Someone asked to create an account with this address.

               To finish, open this link and enter the password you chose when you signed up: {link}

               If you did not ask for this, ignore this email. No account is created unless the link is used with that password.
               """;

        return DeliverAsync(email, Subject, body);
    }
}
