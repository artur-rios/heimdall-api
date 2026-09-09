using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.WebApi.Retention;

/// <summary>
///     Runs <see cref="PseudonymiseAuditActorsCommand" /> on an interval, so the audit trail stops
///     naming people once it no longer needs to (NFR-21).
/// </summary>
public class AuditRetentionService(
    IServiceScopeFactory scopeFactory,
    DataRetentionOptions retention,
    ILogger<AuditRetentionService> logger)
    : ScheduledRetentionService(scopeFactory, retention.PurgeInterval, logger)
{
    protected override string StartupDescription =>
        $"Audit attribution pseudonymisation scheduled every {retention.PurgeInterval}, clearing " +
        $"attributions after {retention.AuditActorRetention} and immediately for erased identities";

    protected override async Task<string?> RunAsync(CommandMediator mediator)
    {
        var result = await mediator
            .ExecuteCommandAsync<PseudonymiseAuditActorsCommand, PseudonymiseAuditActorsCommandOutput>(
                new PseudonymiseAuditActorsCommand());

        if (!result.Success)
        {
            return $"Audit attribution pseudonymisation failed: {string.Join("; ", result.Errors)}";
        }

        var pseudonymised = result.Data!;

        return pseudonymised.EntriesPseudonymised == 0
            ? null
            : $"Cleared the actor attribution from {pseudonymised.EntriesPseudonymised} audit entries";
    }
}
