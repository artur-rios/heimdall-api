using ArturRios.Mediator.Command;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Command.Auditing;

/// <summary>
///     Wraps a command handler so every successful write produces one audit trail entry (NFR-09),
///     without changing the wrapped handler. Registered per-handler by
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
    public async Task<DataOutput<TOutput?>> HandleAsync(TCommand command)
    {
        var result = await inner.HandleAsync(command);

        if (result.Success)
        {
            try
            {
                await auditLogWriter.WriteAsync(typeof(TCommand).Name, ResolveTargetId(result.Data));
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception, "Failed to write audit log entry for {Action} (target {TargetId})",
                    typeof(TCommand).Name, ResolveTargetId(result.Data));
            }
        }

        return result;
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
