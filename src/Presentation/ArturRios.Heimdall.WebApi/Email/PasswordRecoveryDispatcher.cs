using ArturRios.Heimdall.Command.Services;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Works through <see cref="PasswordRecoveryQueue" />: for each request, a fresh DI scope and one
///     <see cref="PasswordRecoveryProcessor" /> run — UC-12's lookup, token and email, after the caller
///     has already been answered.
/// </summary>
/// <remarks>
///     <para>
///         A scope per request, not one for the service's lifetime: the database context and the
///         Mailgun client are scoped, and each request's work should neither see the last one's
///         tracked entities nor depend on an HTTP request's scope that ended when it was answered.
///         That is the difference from firing the send off from the handler, which would have used a
///         client the request scope disposes underneath it.
///     </para>
///     <para>
///         One request at a time. Volume is bounded by the per-address rate limit, and a failure —
///         a database blip, Mailgun refusing — is logged and the next request is taken, as the
///         scheduled passes do: one person's email must not stop the next person's.
///     </para>
/// </remarks>
public sealed class PasswordRecoveryDispatcher(
    PasswordRecoveryQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<PasswordRecoveryDispatcher> logger) : BackgroundService
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
            // Shutdown. Whatever is still queued is dropped; see PasswordRecoveryQueue for why that
            // is acceptable.
        }
    }

    private async Task ProcessAsync(PasswordRecoveryRequest request)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();

            await scope.ServiceProvider.GetRequiredService<PasswordRecoveryProcessor>().ProcessAsync(request);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "A password recovery request could not be processed");
        }
    }
}
