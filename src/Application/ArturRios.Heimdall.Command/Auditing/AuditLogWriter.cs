using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Security;

namespace ArturRios.Heimdall.Command.Auditing;

public class AuditLogWriter(IAsyncRepository<AuditLog> repository, IActorAccessor actorAccessor)
    : IAuditLogWriter
{
    public async Task WriteAsync(string action, Guid? targetId)
    {
        var entry = new AuditLog
        {
            PublicId = Guid.NewGuid(),
            ActorPersonId = actorAccessor.ActorPersonId,
            ActorRole = actorAccessor.ActorRole,
            Action = action,
            TargetId = targetId,
            CreatedAt = DateTime.UtcNow
        };

        await repository.CreateAsync(entry);
    }
}
