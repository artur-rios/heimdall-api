using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests.Auditing;

public class AuditLogWriterTests
{
    private static IActorAccessor Actor(Guid? personId, int? role)
    {
        var actor = new Mock<IActorAccessor>();
        actor.SetupGet(a => a.ActorPersonId).Returns(personId);
        actor.SetupGet(a => a.ActorRole).Returns(role);
        return actor.Object;
    }

    [UnitFact]
    public async Task GivenAuthenticatedActor_WhenWritingEntry_ThenRowCarriesActorActionAndTarget()
    {
        // Given
        var repository = new AsyncFakeRepository<AuditLog>();
        var personId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var writer = new AuditLogWriter(repository, Actor(personId, 2));

        // When
        await writer.WriteAsync("CreateApplicationCommand", targetId, succeeded: true, failureReason: null);

        // Then
        var stored = (await repository.GetAllAsync()).Data!.Single();
        Assert.Equal(personId, stored.ActorPersonId);
        Assert.Equal(2, stored.ActorRole);
        Assert.Equal("CreateApplicationCommand", stored.Action);
        Assert.Equal(targetId, stored.TargetId);
        Assert.NotEqual(Guid.Empty, stored.PublicId);
    }

    [UnitFact]
    public async Task GivenAnonymousActor_WhenWritingEntry_ThenActorFieldsAreNull()
    {
        // Given
        var repository = new AsyncFakeRepository<AuditLog>();
        var writer = new AuditLogWriter(repository, Actor(null, null));

        // When
        await writer.WriteAsync("PasswordRecoveryCommand", null, succeeded: true, failureReason: null);

        // Then
        var stored = (await repository.GetAllAsync()).Data!.Single();
        Assert.Null(stored.ActorPersonId);
        Assert.Null(stored.ActorRole);
        Assert.Null(stored.TargetId);
    }

    [UnitFact]
    public async Task GivenAFailure_WhenWritingEntry_ThenTheOutcomeAndReasonAreStored()
    {
        // Given
        var repository = new AsyncFakeRepository<AuditLog>();
        var writer = new AuditLogWriter(repository, Actor(Guid.NewGuid(), 1));

        // When
        await writer.WriteAsync("DeletePersonCommand", null, succeeded: false, failureReason: "Not authorized.");

        // Then
        var stored = (await repository.GetAllAsync()).Data!.Single();
        Assert.False(stored.Succeeded);
        Assert.Equal("Not authorized.", stored.FailureReason);
    }

    [UnitFact]
    public async Task GivenAnOverlongReason_WhenWritingEntry_ThenItIsTruncatedRatherThanRejected()
    {
        // The column is bounded, and a reason too long for it must not turn a recorded refusal into
        // no record at all — which is the one outcome the trail cannot have.
        var repository = new AsyncFakeRepository<AuditLog>();
        var writer = new AuditLogWriter(repository, Actor(null, null));

        // When
        await writer.WriteAsync("StubCommand", null, succeeded: false, failureReason: new string('x', 900));

        // Then
        var stored = (await repository.GetAllAsync()).Data!.Single();
        Assert.Equal(500, stored.FailureReason!.Length);
    }
}
