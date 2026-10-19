using ArturRios.Heimdall.Command.Auditing;

namespace ArturRios.Heimdall.Command.Tests.Support;

/// <summary>Records the audit entries a handler writes itself, beside the decorator's.</summary>
public sealed class RecordingAuditLogWriter : IAuditLogWriter
{
    public List<(string Action, Guid? TargetId, bool Succeeded)> Entries { get; } = [];

    public Task WriteAsync(string action, Guid? targetId, bool succeeded, string? failureReason)
    {
        Entries.Add((action, targetId, succeeded));

        return Task.CompletedTask;
    }
}
