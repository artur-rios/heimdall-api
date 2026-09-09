using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.WebApi.Retention;

/// <summary>
///     Runs <see cref="PurgeExpiredTokensCommand" /> on an interval, so the single-use token
///     retention period of NFR-19 is enforced without anyone having to call anything.
/// </summary>
/// <remarks>
///     <para>
///         The first tick is one whole interval after start-up, which is deliberate: it keeps the
///         purge clear of migrations and seeding, and it means a container that restarts repeatedly
///         never turns start-up into delete pressure on the tables the login path writes to.
///     </para>
///     <para>
///         A failed run is logged and the loop continues. The pass is a maintenance task, not part
///         of serving a request — a database blip must not take the service down, and the next tick
///         retries whatever was missed, because a row past its retention period stays past it.
///     </para>
///     <para>
///         Every instance runs its own copy. That is intentional and safe: the handler documents why
///         overlapping runs need no coordination, and it is the arrangement NFR-06 asks for, since
///         an instance that assumed another was doing the purging would stop doing it the moment it
///         ran alone.
///     </para>
/// </remarks>
public class TokenRetentionService(
    IServiceScopeFactory scopeFactory,
    DataRetentionOptions retention,
    ILogger<TokenRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Token retention purge scheduled every {Interval}, removing tokens more than {Grace} past expiry",
            retention.PurgeInterval, retention.SingleUseTokenGrace);

        using var timer = new PeriodicTimer(retention.PurgeInterval);

        while (await SafeWaitForNextTickAsync(timer, stoppingToken))
        {
            await RunOnceAsync();
        }
    }

    /// <summary>
    ///     Waits for the next tick, reporting shutdown as a stop rather than letting the
    ///     cancellation surface as an exception the host logs as a fault.
    /// </summary>
    private static async Task<bool> SafeWaitForNextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RunOnceAsync()
    {
        try
        {
            // The mediator is scoped, and so is the DbContext behind it, so the run needs a scope of
            // its own — this service is a singleton and has none.
            using var scope = scopeFactory.CreateScope();

            var mediator = scope.ServiceProvider.GetRequiredService<CommandMediator>();

            var result = await mediator
                .ExecuteCommandAsync<PurgeExpiredTokensCommand, PurgeExpiredTokensCommandOutput>(new PurgeExpiredTokensCommand());

            if (!result.Success)
            {
                logger.LogWarning(
                    "Token retention purge failed: {Errors}", string.Join("; ", result.Errors));

                return;
            }

            var purged = result.Data!;

            // Logged only when it did something, so an hourly no-op does not bury the runs that
            // matter. The audit trail records every run either way (NFR-09).
            if (purged.TotalPurged > 0)
            {
                logger.LogInformation(
                    "Token retention purge removed {Total} rows: {PasswordReset} password reset, " +
                    "{EmailVerification} email verification, {TwoFactorEmail} two-factor email",
                    purged.TotalPurged, purged.PasswordResetTokensPurged,
                    purged.EmailVerificationTokensPurged, purged.TwoFactorEmailCodesPurged);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Token retention purge threw");
        }
    }
}
