using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Shared.Security;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Stand-in <see cref="IRestrictionLiftNotifier" /> for environments with no Mailgun
///     credentials, matching the other logging senders. Production refuses to start without
///     delivery configured, so this never runs there.
/// </summary>
public class LoggingRestrictionLiftNotifier(ILogger<LoggingRestrictionLiftNotifier> logger)
    : IRestrictionLiftNotifier
{
    public Task<bool> NotifyAsync(string email)
    {
        logger.LogInformation("Restriction lift notification issued for {EmailRef}", LogSafeEmail.Reference(email));

        return Task.FromResult(true);
    }
}
