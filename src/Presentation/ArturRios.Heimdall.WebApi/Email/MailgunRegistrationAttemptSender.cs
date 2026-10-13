using ArturRios.Heimdall.Command.Services;
using ArturRios.Messaging.Email;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Delivers UC-47's registration-attempt notice through Mailgun (AF-47b). A failure is logged by
///     <see cref="MailgunSender" /> and never propagated.
/// </summary>
public class MailgunRegistrationAttemptSender(
    IEmailService emailService,
    ILogger<MailgunRegistrationAttemptSender> logger)
    : MailgunSender(emailService, logger), IRegistrationAttemptSender
{
    private const string Subject = "Someone tried to create an account with your address";

    protected override string Purpose => "registration attempt";

    public Task SendAsync(string email, bool googleAccount)
    {
        var instead = googleAccount
            ? "Your account signs in with Google. Use \"Continue with Google\" to sign in."
            : "Sign in with your password, or use \"Forgotten your password?\" if you no longer have it.";

        var body = $"""
                    Someone tried to create a new account with this address. You already have one.

                    If it was you: {instead}

                    If it was not you, you do not need to do anything. Nothing about your account has changed.
                    """;

        return DeliverAsync(email, Subject, body);
    }
}
