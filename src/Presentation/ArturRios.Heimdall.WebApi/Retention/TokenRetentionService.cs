using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.WebApi.Retention;

/// <summary>
///     Runs <see cref="PurgeExpiredTokensCommand" /> on an interval, so the single-use token
///     retention period of NFR-19 is enforced without anyone having to call anything.
/// </summary>
public class TokenRetentionService(
    IServiceScopeFactory scopeFactory,
    DataRetentionOptions retention,
    ILogger<TokenRetentionService> logger)
    : ScheduledPassService(scopeFactory, retention.PurgeInterval, logger)
{
    public override string PassName => "token-retention";

    protected override string StartupDescription =>
        $"Token retention purge scheduled every {retention.PurgeInterval}, removing tokens more than " +
        $"{retention.SingleUseTokenGrace} past expiry and scope owner invitations " +
        $"{retention.ScopeInvitationRetention} after they ended or expired";

    protected override async Task<ScheduledPassResult> RunAsync(IServiceProvider scope)
    {
        var mediator = scope.GetRequiredService<CommandMediator>();

        var result = await mediator
            .ExecuteCommandAsync<PurgeExpiredTokensCommand, PurgeExpiredTokensCommandOutput>(
                new PurgeExpiredTokensCommand());

        if (!result.Success)
        {
            return ScheduledPassResult.Failure("Token retention purge", result.Errors, result.Data?.TotalPurged ?? 0);
        }

        var purged = result.Data!;

        return new ScheduledPassResult(Summarise(purged), false, purged.TotalPurged);
    }

    /// <summary>
    ///     The line logged for a run: <c>null</c> when nothing was removed and nothing is left, otherwise
    ///     the total, the rows still due after the run, and every term of the total.
    /// </summary>
    /// <remarks>
    ///     A term left out makes the parts disagree with the total (#225): a run that purged only
    ///     application credentials once logged "removed 3 rows" over a list of zeroes. The rows still due
    ///     are what a run left at its batch cap (#206); a number that does not fall from run to run is a
    ///     backlog that is not draining.
    /// </remarks>
    public static string? Summarise(PurgeExpiredTokensCommandOutput purged) =>
        purged.TotalPurged == 0 && purged.Backlog == 0
            ? null
            : $"Token retention purge removed {purged.TotalPurged} rows " +
              $"({purged.Backlog} still due after this run): " +
              $"{purged.PasswordResetTokensPurged} password reset, " +
              $"{purged.EmailVerificationTokensPurged} email verification, " +
              $"{purged.TwoFactorEmailCodesPurged} two-factor email, " +
              $"{purged.PendingRegistrationsPurged} pending registration, " +
              $"{purged.ApplicationCredentialsPurged} application credential, " +
              $"{purged.AttemptNoticeBudgetsPurged} attempt-notice budget, " +
              $"{purged.GoogleIdTokenUsesPurged} used Google ID token, " +
              $"{purged.ScopeOwnerInvitationsPurged} scope owner invitation";
}
