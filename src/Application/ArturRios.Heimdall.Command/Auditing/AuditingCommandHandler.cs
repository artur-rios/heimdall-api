using ArturRios.Mediator.Command;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Command.Auditing;

/// <summary>
///     Wraps a command handler so every attempted write produces one audit trail entry (NFR-09),
///     without changing the wrapped handler — plus one more for each side effect a successful
///     command's output reports through <see cref="IAuditedSideEffects" />. Registered per-handler by
///     <see cref="CommandHandlerRegistrationExtensions.AddAuditedCommandHandler{TCommand,TOutput,THandler}" />.
/// </summary>
public class AuditingCommandHandler<TCommand, TOutput>(
    ICommandHandlerAsync<TCommand, TOutput> inner,
    IAuditLogWriter auditLogWriter,
    ILogger<AuditingCommandHandler<TCommand, TOutput>> logger)
    : ICommandHandlerAsync<TCommand, TOutput>
    where TCommand : BaseCommand
    where TOutput : CommandOutput
{
    public async Task<DataOutput<TOutput?>> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        var result = await inner.HandleAsync(command, cancellationToken);

        var action = typeof(TCommand).Name;
        var targetId = ResolveTargetId(result.Data);

        // Refusals are recorded too, and they are usually the entries worth having: a caller
        // repeatedly denied a scope they do not own, or repeatedly failing a password, leaves no
        // other trace anywhere. Only the first error is kept — enough to say why, and all of them
        // are the application's own canonical messages, so nothing a caller submitted is written
        // here.
        await TryWriteAsync(
            action, targetId, result.Success, result.Success ? null : result.Errors.FirstOrDefault());

        // A side effect only happened if the command did, so a refusal records none. One that acted
        // on an entity of its own is recorded on that entity, where a reader of its history looks.
        if (result.Success && result.Data is IAuditedSideEffects sideEffects)
        {
            foreach (var sideEffect in sideEffects.AuditedSideEffects())
            {
                await TryWriteAsync(sideEffect.Action, sideEffect.TargetId ?? targetId, true, null);
            }
        }

        return result;
    }

    private async Task TryWriteAsync(string action, Guid? targetId, bool succeeded, string? failureReason)
    {
        try
        {
            await auditLogWriter.WriteAsync(action, targetId, succeeded, failureReason);
        }
        catch (Exception exception)
        {
            // The trail is a record of what the API did, not part of doing it: a caller whose
            // request succeeded is not told it failed because the entry could not be written.
            logger.LogWarning(
                exception, "Failed to write audit log entry for {Action} (target {TargetId})",
                action, targetId);
        }
    }

    private static Guid? ResolveTargetId(TOutput? output)
    {
        if (output is null)
        {
            return null;
        }

        var property = typeof(TOutput).GetProperty("Id") ?? typeof(TOutput).GetProperty("PublicId");

        return property is not null && property.PropertyType == typeof(Guid)
            ? (Guid?)property.GetValue(output)
            : null;
    }
}
