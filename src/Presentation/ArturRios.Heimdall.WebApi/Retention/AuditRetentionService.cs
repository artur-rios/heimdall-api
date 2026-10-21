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
    public override string PassName => "audit-retention";

    protected override string StartupDescription =>
        $"Audit attribution pseudonymisation scheduled every {retention.PurgeInterval}, clearing " +
        $"attributions and person targets after {retention.AuditActorRetention} and immediately " +
        "for erased identities";

    protected override async Task<ScheduledPassResult> RunAsync(IServiceProvider scope)
    {
        var mediator = scope.GetRequiredService<CommandMediator>();

        var result = await mediator
            .ExecuteCommandAsync<PseudonymiseAuditActorsCommand, PseudonymiseAuditActorsCommandOutput>(
                new PseudonymiseAuditActorsCommand());

        if (!result.Success)
        {
            return ScheduledPassResult.Failure("Audit attribution pseudonymisation", result.Errors);
        }

        var pseudonymised = result.Data!;

        var rows = (long)pseudonymised.EntriesPseudonymised + pseudonymised.TargetsCleared
                   + pseudonymised.TombstonesRemoved;

        return rows == 0
            ? ScheduledPassResult.Quiet
            : new ScheduledPassResult(
                $"Cleared the actor attribution from {pseudonymised.EntriesPseudonymised} audit entries " +
                $"and the person target from {pseudonymised.TargetsCleared}, and removed " +
                $"{pseudonymised.TombstonesRemoved} tombstones of hard-deleted identities",
                false, rows);
    }
}
