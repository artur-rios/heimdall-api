using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Shared.Security;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Fallback <see cref="IEmailChangeNoticeSender" /> for environments without Mailgun, registered
///     beside <see cref="LoggingEmailVerificationSender" />. Logs a reference to the address, never the
///     address itself (NFR-22).
/// </summary>
public class LoggingEmailChangeNoticeSender(ILogger<LoggingEmailChangeNoticeSender> logger)
    : IEmailChangeNoticeSender
{
    public Task SendAsync(string previousEmail)
    {
        logger.LogInformation("Email change notice for {EmailRef}", LogSafeEmail.Reference(previousEmail));

        return Task.CompletedTask;
    }
}
