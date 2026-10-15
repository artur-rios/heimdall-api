using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.WebApi.Retention;

/// <summary>
///     Runs <see cref="PseudonymiseAuditActorsCommand" /> on an interval, so the audit trail stops
///     naming people, as actors or as targets, once it no longer needs to, and drops the tombstones of
///     hard-deleted identities once nothing names them (NFR-21).
/// </summary>
public class AuditRetentionService(
    IServiceScopeFactory scopeFactory,
    DataRetentionOptions retention,
    ILogger<AuditRetentionService> logger)
    : ScheduledPassService(scopeFactory, retention.PurgeInterval, logger)
{
    protected override string StartupDescription =>
        $"Audit attribution pseudonymisation scheduled every {retention.PurgeInterval}, clearing " +
        $"attributions and person targets after {retention.AuditActorRetention} and immediately " +
        "for erased identities";

    protected override async Task<string?> RunAsync(IServiceProvider scope)
    {
        var mediator = scope.GetRequiredService<CommandMediator>();

        var result = await mediator
            .ExecuteCommandAsync<PseudonymiseAuditActorsCommand, PseudonymiseAuditActorsCommandOutput>(
                new PseudonymiseAuditActorsCommand());

        if (!result.Success)
        {
            return $"Audit attribution pseudonymisation failed: {string.Join("; ", result.Errors)}";
        }

        var pseudonymised = result.Data!;

        return pseudonymised.EntriesPseudonymised == 0 && pseudonymised.TargetsCleared == 0
               && pseudonymised.TombstonesRemoved == 0
            ? null
            : $"Cleared the actor attribution from {pseudonymised.EntriesPseudonymised} audit entries " +
              $"and the person target from {pseudonymised.TargetsCleared}, and removed " +
              $"{pseudonymised.TombstonesRemoved} tombstones of hard-deleted identities";
    }
}
