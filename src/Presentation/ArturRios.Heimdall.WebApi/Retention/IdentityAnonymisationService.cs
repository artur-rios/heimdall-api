using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.WebApi.Retention;

/// <summary>
///     Runs <see cref="AnonymiseExpiredDeletionsCommand" /> on an interval, so a logically deleted
///     identity reaches a terminal state instead of sitting in the database indefinitely (NFR-20).
/// </summary>
/// <remarks>
///     This is the pass that actually discharges GDPR Art. 17 and LGPD Art. 16 for an identity, so
///     unlike the token purge it logs every run that changed something, however small.
/// </remarks>
public class IdentityAnonymisationService(
    IServiceScopeFactory scopeFactory,
    DataRetentionOptions retention,
    ILogger<IdentityAnonymisationService> logger)
    : ScheduledRetentionService(scopeFactory, retention.PurgeInterval, logger)
{
    protected override string StartupDescription =>
        $"Identity anonymisation scheduled every {retention.PurgeInterval}, anonymising records " +
        $"{retention.SubjectErasureDeadline} after a requested erasure and " +
        $"{retention.AdministrativeDeletionWindow} after an administrative deletion";

    protected override async Task<string?> RunAsync(CommandMediator mediator)
    {
        var result = await mediator
            .ExecuteCommandAsync<AnonymiseExpiredDeletionsCommand, AnonymiseExpiredDeletionsCommandOutput>(
                new AnonymiseExpiredDeletionsCommand());

        if (!result.Success)
        {
            return $"Identity anonymisation failed: {string.Join("; ", result.Errors)}";
        }

        var anonymised = result.Data!;

        return anonymised.TotalAnonymised == 0
            ? null
            : $"Anonymised {anonymised.TotalAnonymised} identities past their retention window: " +
              $"{anonymised.PersonsAnonymised} persons, {anonymised.GoogleUsersAnonymised} Google Users, " +
              $"removing {anonymised.DependentsRemoved} dependent rows";
    }
}
