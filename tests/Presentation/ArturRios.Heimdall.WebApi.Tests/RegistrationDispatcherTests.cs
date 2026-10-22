using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.WebApi.Email;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for the background half of UC-47: RegistrationQueue and RegistrationDispatcher, mirroring
// PasswordRecoveryDispatcherTests. The handler only queues (AF-47b); these pin that what it queued is then
// worked through, one DI scope per request, that one failure does not stop the next request, and that a
// full queue refuses at once instead of blocking the caller.
public class RegistrationDispatcherTests
{
    /// <summary>Records every address a link was sent to, and can fail for chosen addresses.</summary>
    private sealed class RecordingLinkSender(params string[] failFor) : IRegistrationLinkSender
    {
        public List<string> Recipients { get; } = [];

        public Task SendAsync(string email, string token, string? linkBase)
        {
            if (failFor.Contains(email))
            {
                throw new InvalidOperationException("Mailgun is down");
            }

            lock (Recipients)
            {
                Recipients.Add(email);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class NoopAttemptSender : IRegistrationAttemptSender
    {
        public Task SendAsync(string email, bool googleAccount) => Task.CompletedTask;
    }

    private sealed class AdmittingBudgets : IAttemptNoticeBudgets
    {
        public Task<bool> TryChargeAsync(
            string addressHash, int maxNotices, DateTime now, DateTime windowEndsAt) => Task.FromResult(true);
    }

    private static RegistrationRequest Request(Guid scopeId, string email) =>
        new(scopeId, "Name", email, [1], [2]);

    private static async Task<(ServiceProvider Provider, RegistrationQueue Queue, List<IServiceProvider> Scopes, Guid ScopeId)>
        BuildAsync(IRegistrationLinkSender linkSender)
    {
        var scopesRepository = new AsyncFakeRepository<Scope, long>();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = "open", SelfRegistrationEnabled = true };
        await scopesRepository.CreateAsync(scope);

        var scopes = new List<IServiceProvider>();
        var services = new ServiceCollection();
        var pending = new AsyncFakeRepository<PendingRegistration, long>();

        services.AddSingleton(new RegistrationQueue(NullLogger<RegistrationQueue>.Instance));
        services.AddScoped(provider =>
        {
            lock (scopes)
            {
                scopes.Add(provider);
            }

            return new RegistrationProcessor(
                scopesRepository,
                new AsyncFakeRepository<Person, long>(),
                new AsyncFakeRepository<GoogleUser, long>(),
                pending,
                pending,
                linkSender,
                new NoopAttemptSender(),
                new AdmittingBudgets(),
                new EmailVerificationOptions { TokenLifetime = TimeSpan.FromHours(24) },
                new RegistrationOptions(),
                NullLogger<RegistrationProcessor>.Instance);
        });

        var built = services.BuildServiceProvider();

        return (built, built.GetRequiredService<RegistrationQueue>(), scopes, scope.PublicId);
    }

    private static RegistrationDispatcher DispatcherFor(ServiceProvider provider, RegistrationQueue queue) =>
        new(queue, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<RegistrationDispatcher>.Instance);

    private static Task WaitUntilAsync(Func<bool> condition) =>
        Poll.UntilAsync(condition, "The dispatcher did not process the queue in time.", TimeSpan.FromSeconds(10));

    [UnitFact]
    public async Task GivenQueuedRequests_WhenTheDispatcherRuns_ThenEachIsProcessedInAScopeOfItsOwn()
    {
        // Given
        var links = new RecordingLinkSender();
        var (provider, queue, scopes, scopeId) = await BuildAsync(links);
        await using var _ = provider;
        var dispatcher = DispatcherFor(provider, queue);

        // When
        await dispatcher.StartAsync(CancellationToken.None);
        Assert.True(queue.TryEnqueue(Request(scopeId, "a@test.local")));
        Assert.True(queue.TryEnqueue(Request(scopeId, "b@test.local")));
        await WaitUntilAsync(() => links.Recipients.Count == 2);
        await dispatcher.StopAsync(CancellationToken.None);

        // Then — in order, and never sharing a scope with the request that queued them or each other
        Assert.Equal(["a@test.local", "b@test.local"], links.Recipients);
        Assert.Equal(2, scopes.Distinct().Count());
    }

    [UnitFact]
    public async Task GivenAFailingSend_WhenTheDispatcherRuns_ThenTheNextRequestIsStillProcessed()
    {
        // Given a send that throws for the first address
        var links = new RecordingLinkSender(failFor: "a@test.local");
        var (provider, queue, _, scopeId) = await BuildAsync(links);
        await using var __ = provider;
        var dispatcher = DispatcherFor(provider, queue);

        // When
        await dispatcher.StartAsync(CancellationToken.None);
        queue.TryEnqueue(Request(scopeId, "a@test.local"));
        queue.TryEnqueue(Request(scopeId, "b@test.local"));
        await WaitUntilAsync(() => links.Recipients.Count == 1);
        await dispatcher.StopAsync(CancellationToken.None);

        // Then
        Assert.Equal(["b@test.local"], links.Recipients);
    }

    [UnitFact]
    public async Task GivenARunningDispatcher_WhenItIsStopped_ThenItStopsWithoutThrowing()
    {
        // Given
        var (provider, queue, _, _) = await BuildAsync(new RecordingLinkSender());
        await using var _ = provider;
        var dispatcher = DispatcherFor(provider, queue);
        await dispatcher.StartAsync(CancellationToken.None);

        // When / Then — shutdown while waiting on an empty queue is not an error
        var failure = await Record.ExceptionAsync(() => dispatcher.StopAsync(CancellationToken.None));

        Assert.Null(failure);
        Assert.True(dispatcher.ExecuteTask is not { IsFaulted: true });
    }

    [UnitFact]
    public void GivenAFullQueue_WhenEnqueuing_ThenTheRequestIsRefusedWithoutWaiting()
    {
        // Given a queue nobody is reading, filled to capacity
        var queue = new RegistrationQueue(NullLogger<RegistrationQueue>.Instance);

        for (var i = 0; i < RegistrationQueue.Capacity; i++)
        {
            Assert.True(queue.TryEnqueue(Request(Guid.NewGuid(), $"{i}@test.local")));
        }

        // When / Then — refused at once rather than blocking the request that asked
        Assert.False(queue.TryEnqueue(Request(Guid.NewGuid(), "one-too-many@test.local")));
    }
}
