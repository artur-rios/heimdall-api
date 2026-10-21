using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Heimdall.WebApi.Retention;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     What the three retention passes report once they drain in batches (#206): the rows still due after
///     the run, so a backlog that is not draining is visible, and the rows a failed run had already
///     processed, so the rows-processed counter does not lose the batches before the failure.
/// </summary>
public class RetentionBacklogSummaryTests
{
    private sealed class StubHandler<TCommand, TOutput>(DataOutput<TOutput?> result)
        : ICommandHandlerAsync<TCommand, TOutput>
        where TCommand : BaseCommand
        where TOutput : CommandOutput
    {
        public Task<DataOutput<TOutput?>> HandleAsync(TCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class TokenPass(IServiceScopeFactory factory)
        : TokenRetentionService(factory, new DataRetentionOptions(), NullLogger<TokenRetentionService>.Instance)
    {
        public Task<ScheduledPassResult> Run(IServiceProvider scope) => RunAsync(scope);
    }

    private sealed class AuditPass(IServiceScopeFactory factory)
        : AuditRetentionService(factory, new DataRetentionOptions(), NullLogger<AuditRetentionService>.Instance)
    {
        public Task<ScheduledPassResult> Run(IServiceProvider scope) => RunAsync(scope);
    }

    private sealed class IdentityPass(IServiceScopeFactory factory)
        : IdentityAnonymisationService(
            factory, new DataRetentionOptions(), NullLogger<IdentityAnonymisationService>.Instance)
    {
        public Task<ScheduledPassResult> Run(IServiceProvider scope) => RunAsync(scope);
    }

    private static ServiceProvider Provider<TCommand, TOutput>(DataOutput<TOutput?> handled)
        where TCommand : BaseCommand
        where TOutput : CommandOutput =>
        new ServiceCollection()
            .AddScoped<CommandMediator>()
            .AddScoped<ICommandHandlerAsync<TCommand, TOutput>>(_ => new StubHandler<TCommand, TOutput>(handled))
            .BuildServiceProvider();

    private static Task<ScheduledPassResult> RunTokenPassAsync(DataOutput<PurgeExpiredTokensCommandOutput?> handled)
    {
        var provider = Provider<PurgeExpiredTokensCommand, PurgeExpiredTokensCommandOutput>(handled);

        return new TokenPass(provider.GetRequiredService<IServiceScopeFactory>()).Run(provider);
    }

    private static Task<ScheduledPassResult> RunAuditPassAsync(
        DataOutput<PseudonymiseAuditActorsCommandOutput?> handled)
    {
        var provider = Provider<PseudonymiseAuditActorsCommand, PseudonymiseAuditActorsCommandOutput>(handled);

        return new AuditPass(provider.GetRequiredService<IServiceScopeFactory>()).Run(provider);
    }

    private static Task<ScheduledPassResult> RunIdentityPassAsync(
        DataOutput<AnonymiseExpiredDeletionsCommandOutput?> handled)
    {
        var provider = Provider<AnonymiseExpiredDeletionsCommand, AnonymiseExpiredDeletionsCommandOutput>(handled);

        return new IdentityPass(provider.GetRequiredService<IServiceScopeFactory>()).Run(provider);
    }

    [UnitFact]
    public async Task GivenATokenPurgeThatStoppedAtItsCap_WhenReported_ThenTheSummaryNamesTheBacklog()
    {
        var result = await RunTokenPassAsync(DataOutput<PurgeExpiredTokensCommandOutput?>.New.WithData(
            new PurgeExpiredTokensCommandOutput { PasswordResetTokensPurged = 10000, Backlog = 2500 }));

        Assert.False(result.Failed);
        Assert.Equal(10000, result.RowsProcessed);
        Assert.Contains("2500 still due after this run", result.Summary);
    }

    [UnitFact]
    public async Task GivenATokenPurgeThatFailedAfterSomeBatches_WhenReported_ThenTheRowsItRemovedAreCounted()
    {
        var result = await RunTokenPassAsync(DataOutput<PurgeExpiredTokensCommandOutput?>.New
            .WithData(new PurgeExpiredTokensCommandOutput { PasswordResetTokensPurged = 1000 })
            .WithError("database unavailable"));

        Assert.True(result.Failed);
        Assert.Equal(1000, result.RowsProcessed);
        Assert.Contains("database unavailable", result.Summary);
        Assert.Contains("1000 rows", result.Summary);
    }

    [UnitFact]
    public async Task GivenAnAuditPassThatStoppedAtItsCap_WhenReported_ThenTheSummaryNamesTheBacklog()
    {
        var result = await RunAuditPassAsync(DataOutput<PseudonymiseAuditActorsCommandOutput?>.New.WithData(
            new PseudonymiseAuditActorsCommandOutput { EntriesPseudonymised = 10000, TargetsCleared = 40, Backlog = 70000 }));

        Assert.False(result.Failed);
        Assert.Equal(10040, result.RowsProcessed);
        Assert.Contains("70000 still due after this run", result.Summary);
    }

    [UnitFact]
    public async Task GivenAnAuditPassThatFailedAfterSomeBatches_WhenReported_ThenTheRowsItClearedAreCounted()
    {
        var result = await RunAuditPassAsync(DataOutput<PseudonymiseAuditActorsCommandOutput?>.New
            .WithData(new PseudonymiseAuditActorsCommandOutput { EntriesPseudonymised = 500, TargetsCleared = 3 })
            .WithError("database unavailable"));

        Assert.True(result.Failed);
        Assert.Equal(503, result.RowsProcessed);
        Assert.Contains("database unavailable", result.Summary);
    }

    [UnitFact]
    public async Task GivenAnAnonymisationThatStoppedAtItsCap_WhenReported_ThenTheSummaryNamesTheBacklog()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var result = await RunIdentityPassAsync(DataOutput<AnonymiseExpiredDeletionsCommandOutput?>.New.WithData(
            new AnonymiseExpiredDeletionsCommandOutput { PersonsAnonymised = 2, AnonymisedIds = ids, Backlog = 18000 }));

        Assert.False(result.Failed);
        Assert.Equal(2, result.RowsProcessed);
        Assert.Contains("18000 still due after this run", result.Summary);
        Assert.Contains($"Erasure ledger: {ids[0]},{ids[1]}", result.Summary);
    }

    [UnitFact]
    public async Task GivenARunThatDrainedEverything_WhenReported_ThenTheSummarySaysNothingIsLeft()
    {
        var result = await RunAuditPassAsync(DataOutput<PseudonymiseAuditActorsCommandOutput?>.New.WithData(
            new PseudonymiseAuditActorsCommandOutput { EntriesPseudonymised = 3 }));

        Assert.Contains("0 still due after this run", result.Summary);
    }

    [UnitFact]
    public async Task GivenNothingDoneAndNothingLeft_WhenReported_ThenTheRunIsQuiet()
    {
        var result = await RunAuditPassAsync(
            DataOutput<PseudonymiseAuditActorsCommandOutput?>.New.WithData(new PseudonymiseAuditActorsCommandOutput()));

        Assert.Null(result.Summary);
        Assert.False(result.Failed);
    }
}
