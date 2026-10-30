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
    : ScheduledPassService(scopeFactory, retention.PurgeInterval, logger)
{
    public override string PassName => "identity-anonymisation";

    protected override string StartupDescription =>
        $"Identity anonymisation scheduled every {retention.PurgeInterval}, anonymising records " +
        $"{retention.SubjectErasureDeadline} after a requested erasure and " +
        $"{retention.AdministrativeDeletionWindow} after an administrative deletion";

    protected override async Task<ScheduledPassResult> RunAsync(IServiceProvider scope)
    {
        var mediator = scope.GetRequiredService<CommandMediator>();

        var result = await mediator
            .ExecuteCommandAsync<AnonymiseExpiredDeletionsCommand, AnonymiseExpiredDeletionsCommandOutput>(
                new AnonymiseExpiredDeletionsCommand());

        if (!result.Success)
        {
            // A retry of a blocked request can fail after the due identities were anonymised (#210); the
            // ledger still has to be written then, or those erasures would leave no trace outside the
            // database.
            return result.Data is { TotalAnonymised: > 0 } done
                ? new ScheduledPassResult(
                    $"Identity anonymisation failed: {string.Join("; ", result.Errors)}. " +
                    $"Anonymised {done.TotalAnonymised} identities nonetheless. {Ledger(done)}",
                    true, done.TotalAnonymised)
                : ScheduledPassResult.Failure("Identity anonymisation", result.Errors);
        }

        var anonymised = result.Data!;

        // The identifiers are named, not just counted: this line is the erasure ledger (NFR-25),
        // and it is what makes a restore reconcilable. A backup taken before a subject asked
        // reinstates them with no trace of the request, so the list has to survive somewhere the
        // restore cannot roll back — which means outside the database, in the logs.
        //
        // Safe to write and to ship off the host precisely because these identities are now
        // anonymised: a PublicId that resolves to nobody is not personal data.
        //
        // The identities still due are what the run left at its batch cap (#206): a number that does not
        // fall from run to run is a backlog that is not draining, and deadlines passing.
        return anonymised.TotalAnonymised == 0 && anonymised.Backlog == 0
            ? ScheduledPassResult.Quiet
            : new ScheduledPassResult(
                $"Anonymised {anonymised.TotalAnonymised} identities past their retention window: " +
                $"{anonymised.PersonsAnonymised} persons, {anonymised.GoogleUsersAnonymised} Google Users, " +
                $"removing {anonymised.DependentsRemoved} dependent rows; " +
                $"{anonymised.Backlog?.ToString() ?? "an unknown number"} still due after this run. " +
                Ledger(anonymised),
                false, anonymised.TotalAnonymised);
    }

    private static string Ledger(AnonymiseExpiredDeletionsCommandOutput anonymised) =>
        $"Erasure ledger: {string.Join(",", anonymised.AnonymisedIds)}";
}
