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
    protected override string StartupDescription =>
        $"Token retention purge scheduled every {retention.PurgeInterval}, removing tokens more than " +
        $"{retention.SingleUseTokenGrace} past expiry";

    protected override async Task<string?> RunAsync(IServiceProvider scope)
    {
        var mediator = scope.GetRequiredService<CommandMediator>();

        var result = await mediator
            .ExecuteCommandAsync<PurgeExpiredTokensCommand, PurgeExpiredTokensCommandOutput>(
                new PurgeExpiredTokensCommand());

        if (!result.Success)
        {
            return $"Token retention purge failed: {string.Join("; ", result.Errors)}";
        }

        var purged = result.Data!;

        return purged.TotalPurged == 0
            ? null
            : $"Token retention purge removed {purged.TotalPurged} rows: " +
              $"{purged.PasswordResetTokensPurged} password reset, " +
              $"{purged.EmailVerificationTokensPurged} email verification, " +
              $"{purged.TwoFactorEmailCodesPurged} two-factor email";
    }
}
