using ArturRios.Heimdall.Command.Services;
using ArturRios.Messaging.Email;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Delivers UC-08's email-change notice to the previous address through Mailgun (FR-PE-15). A
///     failure is logged by <see cref="MailgunSender" /> and never propagated: the change has been
///     made, and failing the response would not undo it.
/// </summary>
/// <remarks>
///     The body names neither the new address nor any link. See <see cref="IEmailChangeNoticeSender" />.
/// </remarks>
public class MailgunEmailChangeNoticeSender(
    IEmailService emailService,
    ILogger<MailgunEmailChangeNoticeSender> logger)
    : MailgunSender(emailService, logger), IEmailChangeNoticeSender
{
    private const string Subject = "The email address on your account was changed";

    protected override string Purpose => "email change notice";

    public Task SendAsync(string previousEmail)
    {
        const string body = """
                            The email address on your account was changed, and this address no longer
                            receives its messages.

                            If you made this change, you do not need to do anything.

                            If you did not, contact an administrator straight away.
                            """;

        return DeliverAsync(previousEmail, Subject, body);
    }
}
