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
        await writer.WriteAsync("CreateApplicationCommand", targetId);

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
        await writer.WriteAsync("PasswordRecoveryCommand", null);

        // Then
        var stored = (await repository.GetAllAsync()).Data!.Single();
        Assert.Null(stored.ActorPersonId);
        Assert.Null(stored.ActorRole);
        Assert.Null(stored.TargetId);
    }
}
