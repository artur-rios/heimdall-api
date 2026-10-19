using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Security;

namespace ArturRios.Heimdall.Command.Auditing;

public class AuditLogWriter(IAsyncRepository<AuditLog, long> repository, IActorAccessor actorAccessor)
    : IAuditLogWriter
{
    /// <summary>Longest failure reason stored; matches AUDIT_LOG.failure_reason's column.</summary>
    private const int MaxFailureReasonLength = 500;

    public async Task WriteAsync(string action, Guid? targetId, bool succeeded, string? failureReason)
    {
        var entry = new AuditLog
        {
            PublicId = Guid.NewGuid(),
            ActorPersonId = actorAccessor.ActorPersonId,
            ActorRole = actorAccessor.ActorRole,
            Action = action,
            TargetId = targetId,
            Succeeded = succeeded,
            // Truncated rather than allowed to fail the insert: a reason too long for the column
            // would otherwise turn a recorded refusal into no record at all, which is the one
            // outcome the trail must not have.
            FailureReason = failureReason is { Length: > MaxFailureReasonLength }
                ? failureReason[..MaxFailureReasonLength]
                : failureReason,
            CreatedAt = DateTime.UtcNow
        };

        // The repository reports a failed insert in its output instead of throwing. Surface it, so
        // the caller's catch-and-warn sees it; the message carries the action and the repository's
        // own error text only, never the entry's actor or other personal data.
        var output = await repository.CreateAsync(entry);

        if (!output.Success)
        {
            throw new InvalidOperationException(
                $"Failed to save audit log entry for {action}: {string.Join("; ", output.Errors)}");
        }
    }
}
