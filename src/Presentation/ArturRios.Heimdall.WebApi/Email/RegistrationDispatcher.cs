using ArturRios.Heimdall.Command.Services;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Works through <see cref="RegistrationQueue" />: one fresh DI scope and one
///     <see cref="RegistrationProcessor" /> run per request, after the caller has been answered. One
///     request at a time; a failure is logged and the next request taken. See
///     <see cref="PasswordRecoveryDispatcher" />, which this mirrors.
/// </summary>
public sealed class RegistrationDispatcher(
    RegistrationQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<RegistrationDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(request);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. Whatever is still queued is dropped, as in the recovery queue.
        }
    }

    private async Task ProcessAsync(RegistrationRequest request)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();

            await scope.ServiceProvider.GetRequiredService<RegistrationProcessor>().ProcessAsync(request);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "A registration request could not be processed");
        }
    }
}
