using ArturRios.Messaging.Email;

namespace ArturRios.IdentityManager.WebApi.Email;

/// <summary>
///     Shared delivery path for the transactional emails this API sends through
///     <see cref="ArturRios.Messaging" />'s Mailgun service. Subclasses supply the subject and body;
///     this class owns the one rule both of them must obey.
/// </summary>
/// <remarks>
///     <para>
///         <b>A send failure is logged, never propagated.</b> Both callers sit on endpoints that
///         must answer identically whether or not the address belongs to anyone — UC-12's AF-12a
///         most explicitly. Letting a Mailgun outage, a bad API key, or a dropped connection surface
///         as a 500 would turn "the email was delivered" into an observable difference, and hand an
///         anonymous caller the account-enumeration oracle the specification is built to deny them.
///     </para>
///     <para>
///         The token survives the failure: it is already persisted by the time delivery is
///         attempted, so a caller can request another email, and UC-15 can resend the verification
///         one.
///     </para>
/// </remarks>
public abstract class MailgunSender(IEmailService emailService, ILogger logger)
{
    /// <summary>What this sender delivers, for log messages. E.g. "password reset".</summary>
    protected abstract string Purpose { get; }

    protected async Task DeliverAsync(string email, string subject, string body)
    {
        try
        {
            var result = await emailService.SendEmailAsync(email, subject, body);

            if (!result.Success)
            {
                logger.LogError(
                    "Mailgun refused the {Purpose} email for {Email}: {Errors}",
                    Purpose, email, string.Join(" | ", result.Errors));

                return;
            }

            logger.LogInformation("Sent the {Purpose} email to {Email}", Purpose, email);
        }
        catch (Exception exception)
        {
            // Reaching Mailgun is I/O: it can time out, refuse the connection, or fail DNS. None of
            // that is the caller's business, and none of it may change the response they get.
            logger.LogError(
                exception, "Could not deliver the {Purpose} email to {Email}", Purpose, email);
        }
    }
}
