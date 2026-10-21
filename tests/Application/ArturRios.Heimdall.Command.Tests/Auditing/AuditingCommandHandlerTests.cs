using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ArturRios.Heimdall.Command.Tests.Auditing;

public class StubCommand : BaseCommand;

public class StubOutput : CommandOutput
{
    public Guid Id { get; set; }
}

public class StubOutputWithSideEffects : CommandOutput, IAuditedSideEffects
{
    public Guid Id { get; set; }

    public List<string> SideEffects { get; set; } = [];

    public IEnumerable<string> AuditedSideEffects() => SideEffects;
}

public class AuditingCommandHandlerTests
{
    private static ILogger<AuditingCommandHandler<StubCommand, StubOutput>> NullLogger() =>
        NullLogger<AuditingCommandHandler<StubCommand, StubOutput>>.Instance;

    private static AuditingCommandHandler<StubCommand, StubOutputWithSideEffects> SideEffectsHandler(
        DataOutput<StubOutputWithSideEffects?> result, IAuditLogWriter writer)
    {
        var inner = new Mock<ICommandHandlerAsync<StubCommand, StubOutputWithSideEffects>>();
        inner.Setup(h => h.HandleAsync(It.IsAny<StubCommand>())).ReturnsAsync(result);

        return new AuditingCommandHandler<StubCommand, StubOutputWithSideEffects>(
            inner.Object, writer, NullLogger<AuditingCommandHandler<StubCommand, StubOutputWithSideEffects>>.Instance);
    }

    [UnitFact]
    public async Task GivenSuccessfulInnerHandler_WhenHandling_ThenWriterIsCalledWithActionAndTargetId()
    {
        // Given an inner handler that succeeds and returns an id-bearing output
        var targetId = Guid.NewGuid();
        var inner = new Mock<ICommandHandlerAsync<StubCommand, StubOutput>>();
        inner.Setup(h => h.HandleAsync(It.IsAny<StubCommand>()))
            .ReturnsAsync(DataOutput<StubOutput?>.New.WithData(new StubOutput { Id = targetId }));
        var writer = new Mock<IAuditLogWriter>();
        var handler = new AuditingCommandHandler<StubCommand, StubOutput>(inner.Object, writer.Object, NullLogger());

        // When
        var result = await handler.HandleAsync(new StubCommand());

        // Then
        Assert.True(result.Success);
        writer.Verify(w => w.WriteAsync(nameof(StubCommand), targetId, true, null), Times.Once);
    }

    [UnitFact]
    public async Task GivenFailedInnerHandler_WhenHandling_ThenTheRefusalIsRecorded()
    {
        // Given an inner handler that refuses the command. This used to write nothing at all, which
        // left the trail describing only what the API allowed — and a caller repeatedly denied a
        // scope they do not own, or repeatedly failing a password, has no other trace anywhere
        // (NFR-09).
        var inner = new Mock<ICommandHandlerAsync<StubCommand, StubOutput>>();
        inner.Setup(h => h.HandleAsync(It.IsAny<StubCommand>()))
            .ReturnsAsync(DataOutput<StubOutput?>.New.WithError("invalid"));
        var writer = new Mock<IAuditLogWriter>();
        var handler = new AuditingCommandHandler<StubCommand, StubOutput>(inner.Object, writer.Object, NullLogger());

        // When
        var result = await handler.HandleAsync(new StubCommand());

        // Then — recorded as a refusal, carrying why
        Assert.False(result.Success);
        writer.Verify(w => w.WriteAsync(nameof(StubCommand), null, false, "invalid"), Times.Once);
    }

    [UnitFact]
    public async Task GivenSeveralErrors_WhenHandling_ThenOnlyTheFirstIsRecorded()
    {
        // One reason is enough to say why, and the field is bounded. All of them are the
        // application's own canonical messages, so none of this is caller-supplied text.
        var inner = new Mock<ICommandHandlerAsync<StubCommand, StubOutput>>();
        inner.Setup(h => h.HandleAsync(It.IsAny<StubCommand>()))
            .ReturnsAsync(DataOutput<StubOutput?>.New.WithErrors(["first", "second"]));
        var writer = new Mock<IAuditLogWriter>();
        var handler = new AuditingCommandHandler<StubCommand, StubOutput>(inner.Object, writer.Object, NullLogger());

        // When
        await handler.HandleAsync(new StubCommand());

        // Then
        writer.Verify(w => w.WriteAsync(nameof(StubCommand), null, false, "first"), Times.Once);
    }

    [UnitFact]
    public async Task GivenAuditInsertFails_WhenHandling_ThenResultIsUnchangedAndWarningIsLogged()
    {
        // Given a real writer whose repository reports the failed insert in its output rather than
        // throwing; this used to vanish without a trace
        var targetId = Guid.NewGuid();
        var inner = new Mock<ICommandHandlerAsync<StubCommand, StubOutput>>();
        inner.Setup(h => h.HandleAsync(It.IsAny<StubCommand>()))
            .ReturnsAsync(DataOutput<StubOutput?>.New.WithData(new StubOutput { Id = targetId }));
        var repository = new Mock<IAsyncRepository<AuditLog, long>>();
        repository.Setup(r => r.CreateAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataOutput<long>.New.WithError("insert failed"));
        var writer = new AuditLogWriter(repository.Object, new Mock<IActorAccessor>().Object);
        var logger = new Mock<ILogger<AuditingCommandHandler<StubCommand, StubOutput>>>();
        var handler = new AuditingCommandHandler<StubCommand, StubOutput>(inner.Object, writer, logger.Object);

        // When
        var result = await handler.HandleAsync(new StubCommand());

        // Then the command's answer is untouched and the failure was logged
        Assert.True(result.Success);
        Assert.Equal(targetId, result.Data!.Id);
        logger.Verify(
            l => l.Log(
                LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.Is<Exception>(e => e.Message.Contains("insert failed")),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [UnitFact]
    public async Task GivenWriterThrows_WhenHandling_ThenOriginalSuccessfulResultIsStillReturned()
    {
        // Given a writer that throws — an audit-logging outage must not fail the underlying write
        var inner = new Mock<ICommandHandlerAsync<StubCommand, StubOutput>>();
        inner.Setup(h => h.HandleAsync(It.IsAny<StubCommand>()))
            .ReturnsAsync(DataOutput<StubOutput?>.New.WithData(new StubOutput { Id = Guid.NewGuid() }));
        var writer = new Mock<IAuditLogWriter>();
        writer.Setup(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("db unavailable"));
        var handler = new AuditingCommandHandler<StubCommand, StubOutput>(inner.Object, writer.Object, NullLogger());

        // When
        var result = await handler.HandleAsync(new StubCommand());

        // Then
        Assert.True(result.Success);
    }

    [UnitFact]
    public async Task GivenNullOutputData_WhenHandling_ThenWriterIsCalledWithNullTargetId()
    {
        // Given a successful result carrying a null Data (e.g. a command whose output is empty)
        var inner = new Mock<ICommandHandlerAsync<StubCommand, StubOutput>>();
        inner.Setup(h => h.HandleAsync(It.IsAny<StubCommand>()))
            .ReturnsAsync(DataOutput<StubOutput?>.New.WithData((StubOutput?)null));
        var writer = new Mock<IAuditLogWriter>();
        var handler = new AuditingCommandHandler<StubCommand, StubOutput>(inner.Object, writer.Object, NullLogger());

        // When
        await handler.HandleAsync(new StubCommand());

        // Then
        writer.Verify(w => w.WriteAsync(nameof(StubCommand), null, true, null), Times.Once);
    }

    [UnitFact]
    public async Task GivenAnOutputReportingASideEffect_WhenHandling_ThenItIsRecordedAsItsOwnEntryOnTheSameTarget()
    {
        // Given a command that also carried out an action with a use case of its own — as an erasure
        // request lifts the subject's restriction (UC-42) — the trail must show that action under
        // its own name, or a reader looking for it would never find it
        var targetId = Guid.NewGuid();
        var writer = new Mock<IAuditLogWriter>();
        var handler = SideEffectsHandler(
            DataOutput<StubOutputWithSideEffects?>.New.WithData(
                new StubOutputWithSideEffects { Id = targetId, SideEffects = ["LiftSomethingCommand"] }),
            writer.Object);

        // When
        await handler.HandleAsync(new StubCommand());

        // Then — the command's own entry, and one for the side effect, both on the same target
        writer.Verify(w => w.WriteAsync(nameof(StubCommand), targetId, true, null), Times.Once);
        writer.Verify(w => w.WriteAsync("LiftSomethingCommand", targetId, true, null), Times.Once);
        writer.VerifyNoOtherCalls();
    }

    [UnitFact]
    public async Task GivenAnOutputReportingNoSideEffect_WhenHandling_ThenOnlyTheCommandIsRecorded()
    {
        var writer = new Mock<IAuditLogWriter>();
        var handler = SideEffectsHandler(
            DataOutput<StubOutputWithSideEffects?>.New.WithData(new StubOutputWithSideEffects { Id = Guid.NewGuid() }),
            writer.Object);

        await handler.HandleAsync(new StubCommand());

        writer.Verify(
            w => w.WriteAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>()),
            Times.Once);
    }

    [UnitFact]
    public async Task GivenARefusedCommandWhoseOutputReportsASideEffect_WhenHandling_ThenOnlyTheRefusalIsRecorded()
    {
        // A side effect only happened if the command did
        var writer = new Mock<IAuditLogWriter>();
        var handler = SideEffectsHandler(
            DataOutput<StubOutputWithSideEffects?>.New
                .WithData(new StubOutputWithSideEffects { Id = Guid.NewGuid(), SideEffects = ["LiftSomethingCommand"] })
                .WithError("refused"),
            writer.Object);

        await handler.HandleAsync(new StubCommand());

        writer.Verify(
            w => w.WriteAsync(nameof(StubCommand), It.IsAny<Guid?>(), false, "refused"), Times.Once);
        writer.Verify(
            w => w.WriteAsync("LiftSomethingCommand", It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>()),
            Times.Never);
    }

    [UnitFact]
    public async Task GivenTheCommandEntryFailsToWrite_WhenHandling_ThenTheSideEffectIsStillRecorded()
    {
        // Each entry is written on its own, so losing one does not lose the other
        var targetId = Guid.NewGuid();
        var writer = new Mock<IAuditLogWriter>();
        writer.Setup(w => w.WriteAsync(nameof(StubCommand), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("db unavailable"));
        var handler = SideEffectsHandler(
            DataOutput<StubOutputWithSideEffects?>.New.WithData(
                new StubOutputWithSideEffects { Id = targetId, SideEffects = ["LiftSomethingCommand"] }),
            writer.Object);

        var result = await handler.HandleAsync(new StubCommand());

        Assert.True(result.Success);
        writer.Verify(w => w.WriteAsync("LiftSomethingCommand", targetId, true, null), Times.Once);
    }

    private static AuditingCommandHandler<StubCommand, StubOutput> ThrowingHandler(
        Exception exception, IAuditLogWriter writer)
    {
        var inner = new Mock<ICommandHandlerAsync<StubCommand, StubOutput>>();
        inner.Setup(h => h.HandleAsync(It.IsAny<StubCommand>(), It.IsAny<CancellationToken>())).ThrowsAsync(exception);

        return new AuditingCommandHandler<StubCommand, StubOutput>(inner.Object, writer, NullLogger());
    }

    [UnitFact]
    public async Task GivenTheHashGateSheds_WhenHandling_ThenTheRefusalIsRecordedWithTheShedReasonAndRethrown()
    {
        // Given a login the saturated hash gate refused (TH-03). The exception used to propagate past
        // the decorator, so the shed attempt left no entry and CREDENTIAL_VERIFICATION_SHEDDING,
        // which counts entries carrying this reason, could never fire (#201).
        var exception = new PasswordHashGateSaturatedException(TimeSpan.FromSeconds(10));
        var writer = new Mock<IAuditLogWriter>();
        var handler = ThrowingHandler(exception, writer.Object);

        // When / Then — the refusal still reaches the filter that answers 503
        var thrown = await Assert.ThrowsAsync<PasswordHashGateSaturatedException>(
            () => handler.HandleAsync(new StubCommand()));

        Assert.Same(exception, thrown);
        writer.Verify(
            w => w.WriteAsync(
                nameof(StubCommand), null, false, AuthMessages.AuthenticationTemporarilyUnavailable),
            Times.Once);
    }

    [UnitFact]
    public async Task GivenAnyOtherException_WhenHandling_ThenAFailedEntryWithTheFixedReasonIsRecordedAndItPropagates()
    {
        // The exception's own text is never written: it may carry provider or caller-supplied detail
        var exception = new InvalidOperationException("connection to db.internal:5432 refused");
        var writer = new Mock<IAuditLogWriter>();
        var handler = ThrowingHandler(exception, writer.Object);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new StubCommand()));

        Assert.Same(exception, thrown);
        writer.Verify(
            w => w.WriteAsync(nameof(StubCommand), null, false, AuditMessages.CommandFailed), Times.Once);
    }

    [UnitFact]
    public async Task GivenTheRefusalEntryFailsToWrite_WhenHandling_ThenTheOriginalExceptionIsStillThrown()
    {
        // The audit write in the catch must not mask what actually went wrong
        var exception = new InvalidOperationException("the command failed");
        var writer = new Mock<IAuditLogWriter>();
        writer.Setup(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ThrowsAsync(new TimeoutException("audit db unavailable"));
        var handler = ThrowingHandler(exception, writer.Object);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new StubCommand()));

        Assert.Same(exception, thrown);
    }
}
