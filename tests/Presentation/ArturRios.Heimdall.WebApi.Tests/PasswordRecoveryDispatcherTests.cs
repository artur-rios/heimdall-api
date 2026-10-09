using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Email;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for the background half of UC-12: PasswordRecoveryQueue and PasswordRecoveryDispatcher.
// The handler only queues (AF-12a); these pin that what it queued is then actually worked through,
// one DI scope per request, and that one failure does not stop the next person's email.
public class PasswordRecoveryDispatcherTests
{
    /// <summary>Records every person a token was issued for, and can fail for chosen addresses.</summary>
    private sealed class RecordingResetService(params string[] failFor) : IPasswordResetService
    {
        public List<string> Recipients { get; } = [];

        public Task IssueAndSendAsync(Person person)
        {
            if (failFor.Contains(person.Email))
            {
                throw new InvalidOperationException("Mailgun is down");
            }

            lock (Recipients)
            {
                Recipients.Add(person.Email);
            }

            return Task.CompletedTask;
        }
    }

    private static async Task<AsyncFakeRepository<Person, long>> AdminsAsync(params string[] emails)
    {
        var persons = new AsyncFakeRepository<Person, long>();

        foreach (var email in emails)
        {
            await persons.CreateAsync(new Person
            {
                PublicId = Guid.NewGuid(),
                Name = email,
                Email = email,
                RoleId = (long)Roles.SystemAdmin
            });
        }

        return persons;
    }

    private static (ServiceProvider Provider, PasswordRecoveryQueue Queue, List<IServiceProvider> Scopes) Build(
        AsyncFakeRepository<Person, long> persons, IPasswordResetService passwordReset)
    {
        var scopes = new List<IServiceProvider>();
        var services = new ServiceCollection();

        services.AddSingleton(new PasswordRecoveryQueue(NullLogger<PasswordRecoveryQueue>.Instance));
        services.AddSingleton<IAsyncReadOnlyRepository<Person, long>>(persons);
        services.AddSingleton(passwordReset);
        services.AddScoped(provider =>
        {
            lock (scopes)
            {
                scopes.Add(provider);
            }

            return new PasswordRecoveryProcessor(
                provider.GetRequiredService<IAsyncReadOnlyRepository<Person, long>>(),
                provider.GetRequiredService<IPasswordResetService>());
        });

        var provider = services.BuildServiceProvider();

        return (provider, provider.GetRequiredService<PasswordRecoveryQueue>(), scopes);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The dispatcher did not process the queue in time.");
            await Task.Delay(20);
        }
    }

    [UnitFact]
    public async Task GivenQueuedRequests_WhenTheDispatcherRuns_ThenEachIsProcessedInAScopeOfItsOwn()
    {
        // Given
        var passwordReset = new RecordingResetService();
        var (provider, queue, scopes) = Build(await AdminsAsync("a@test.local", "b@test.local"), passwordReset);
        await using var _ = provider;
        var dispatcher = new PasswordRecoveryDispatcher(
            queue, provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PasswordRecoveryDispatcher>.Instance);

        // When
        await dispatcher.StartAsync(CancellationToken.None);
        Assert.True(queue.TryEnqueue(new PasswordRecoveryRequest("a@test.local", null)));
        Assert.True(queue.TryEnqueue(new PasswordRecoveryRequest("b@test.local", null)));
        await WaitUntilAsync(() => passwordReset.Recipients.Count == 2);
        await dispatcher.StopAsync(CancellationToken.None);

        // Then — in order, and never sharing a scope (so never a DbContext or an HttpClient) with the
        // request that queued them, or with each other
        Assert.Equal(["a@test.local", "b@test.local"], passwordReset.Recipients);
        Assert.Equal(2, scopes.Distinct().Count());
    }

    [UnitFact]
    public async Task GivenAFailingSend_WhenTheDispatcherRuns_ThenTheNextRequestIsStillProcessed()
    {
        // Given a send that throws for the first address
        var passwordReset = new RecordingResetService(failFor: "a@test.local");
        var (provider, queue, _) = Build(await AdminsAsync("a@test.local", "b@test.local"), passwordReset);
        await using var __ = provider;
        var dispatcher = new PasswordRecoveryDispatcher(
            queue, provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PasswordRecoveryDispatcher>.Instance);

        // When
        await dispatcher.StartAsync(CancellationToken.None);
        queue.TryEnqueue(new PasswordRecoveryRequest("a@test.local", null));
        queue.TryEnqueue(new PasswordRecoveryRequest("b@test.local", null));
        await WaitUntilAsync(() => passwordReset.Recipients.Count == 1);
        await dispatcher.StopAsync(CancellationToken.None);

        // Then
        Assert.Equal(["b@test.local"], passwordReset.Recipients);
    }

    [UnitFact]
    public void GivenAFullQueue_WhenEnqueuing_ThenTheRequestIsRefusedWithoutWaiting()
    {
        // Given a queue nobody is reading, filled to capacity
        var queue = new PasswordRecoveryQueue(NullLogger<PasswordRecoveryQueue>.Instance);

        for (var i = 0; i < PasswordRecoveryQueue.Capacity; i++)
        {
            Assert.True(queue.TryEnqueue(new PasswordRecoveryRequest($"{i}@test.local", null)));
        }

        // When / Then — refused at once rather than blocking the request that asked
        Assert.False(queue.TryEnqueue(new PasswordRecoveryRequest("one-too-many@test.local", null)));
    }
}
