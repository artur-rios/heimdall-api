using ArturRios.Heimdall.Command.Auditing;
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

public class AuditingCommandHandlerTests
{
    private static ILogger<AuditingCommandHandler<StubCommand, StubOutput>> NullLogger() =>
        NullLogger<AuditingCommandHandler<StubCommand, StubOutput>>.Instance;

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
}
