using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Heimdall.WebApi.Retention;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     What <see cref="IdentityAnonymisationService" /> reports for a run (#210): the erasure ledger
///     (NFR-25) has to be written whenever identities were anonymised, even if the run also failed.
/// </summary>
public class IdentityAnonymisationServiceTests
{
    private sealed class StubHandler(DataOutput<AnonymiseExpiredDeletionsCommandOutput?> result)
        : ICommandHandlerAsync<AnonymiseExpiredDeletionsCommand, AnonymiseExpiredDeletionsCommandOutput>
    {
        public Task<DataOutput<AnonymiseExpiredDeletionsCommandOutput?>> HandleAsync(
            AnonymiseExpiredDeletionsCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class Service(IServiceScopeFactory factory)
        : IdentityAnonymisationService(
            factory, new DataRetentionOptions(), NullLogger<IdentityAnonymisationService>.Instance)
    {
        public Task<ScheduledPassResult> Run(IServiceProvider scope) => RunAsync(scope);
    }

    private static Task<ScheduledPassResult> RunAsync(DataOutput<AnonymiseExpiredDeletionsCommandOutput?> handled)
    {
        var provider = new ServiceCollection()
            .AddScoped<CommandMediator>()
            .AddScoped<ICommandHandlerAsync<AnonymiseExpiredDeletionsCommand, AnonymiseExpiredDeletionsCommandOutput>>(
                _ => new StubHandler(handled))
            .BuildServiceProvider();

        return new Service(provider.GetRequiredService<IServiceScopeFactory>()).Run(provider);
    }

    [UnitFact]
    public async Task GivenAFailedRunThatAnonymisedIdentities_WhenReported_ThenTheLedgerNamesExactlyThem()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var data = new AnonymiseExpiredDeletionsCommandOutput { PersonsAnonymised = 2, AnonymisedIds = ids };

        var result = await RunAsync(
            DataOutput<AnonymiseExpiredDeletionsCommandOutput?>.New.WithData(data).WithError("retry failed"));

        Assert.True(result.Failed);
        Assert.Equal(2, result.RowsProcessed);
        Assert.Contains($"Erasure ledger: {ids[0]},{ids[1]}", result.Summary);
        Assert.Contains("retry failed", result.Summary);
    }

    [UnitFact]
    public async Task GivenAFailedRunWithNothingAnonymised_WhenReported_ThenThereIsNoLedger()
    {
        var result = await RunAsync(DataOutput<AnonymiseExpiredDeletionsCommandOutput?>.New.WithError("boom"));

        Assert.True(result.Failed);
        Assert.Equal(0, result.RowsProcessed);
        Assert.DoesNotContain("Erasure ledger", result.Summary);
    }
}
