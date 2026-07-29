using ArturRios.IdentityManager.Command.Services;

namespace ArturRios.IdentityManager.WebApi.Security;

/// <summary>
///     UC-06 stub for <see cref="IEmailVerificationSender" />: logs the recipient and token instead of
///     delivering a real email. Real delivery is deferred to a dedicated email-infrastructure change.
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
