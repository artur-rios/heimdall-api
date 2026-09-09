using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Messaging.Email;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Sends GDPR Art. 18(3)'s notification through Mailgun, and reports whether it was accepted.
/// </summary>
/// <remarks>
///     Deliberately does not extend <see cref="MailgunSender" />. That base class swallows delivery
///     failures on purpose, which is right for every other email this API sends and wrong for this
///     one — see <see cref="IRestrictionLiftNotifier" />.
/// </remarks>
public class MailgunRestrictionLiftNotifier(
    IEmailService emailService,
    ILogger<MailgunRestrictionLiftNotifier> logger) : IRestrictionLiftNotifier
{
    private const string Subject = "The restriction on your account is being lifted";

    public async Task<bool> NotifyAsync(string email)
    {
        const string body = """
                            You asked us to restrict processing of your data, and that restriction is
                            being lifted.

                            Your account will work normally again. If you did not expect this, or you
                            believe the matter that led to the restriction is unresolved, reply to
                            this message.
                            """;

        try
        {
            var result = await emailService.SendEmailAsync(email, Subject, body);

            if (!result.Success)
            {
                logger.LogError(
                    "Mailgun refused the restriction lift notification for {EmailRef}: {Errors}",
                    LogSafeEmail.Reference(email), string.Join(" | ", result.Errors));
            }

            return result.Success;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception, "Could not deliver the restriction lift notification to {EmailRef}",
                LogSafeEmail.Reference(email));

            return false;
        }
    }
}
