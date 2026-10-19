using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Shared.Security;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Fallback <see cref="IScopeInvitationNoticeSender" /> for environments without Mailgun, registered beside
///     <see cref="LoggingEmailChangeNoticeSender" />. Logs a reference to the address, never the address itself
///     or the names (NFR-22).
/// </summary>
public class LoggingScopeInvitationNoticeSender(ILogger<LoggingScopeInvitationNoticeSender> logger)
    : IScopeInvitationNoticeSender
{
    public Task SendInvitationAsync(string inviteeEmail, string inviterName, string scopeName, DateTime expiresAt)
    {
        logger.LogInformation("Scope invitation notice for {EmailRef}", LogSafeEmail.Reference(inviteeEmail));

        return Task.CompletedTask;
    }

    public Task SendAnswerAsync(string inviterEmail, string inviteeName, string scopeName, bool accepted)
    {
        logger.LogInformation(
            "Scope invitation answer notice for {EmailRef} (accepted: {Accepted})",
            LogSafeEmail.Reference(inviterEmail), accepted);

        return Task.CompletedTask;
    }
}
