using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Command.Auditing;

/// <summary>
///     Wraps a command handler so every attempted write produces one audit trail entry (NFR-09),
///     without changing the wrapped handler — including one that throws instead of answering, which is
///     recorded as a refusal and rethrown — plus one more for each side effect a successful
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
        var action = typeof(TCommand).Name;

        DataOutput<TOutput?> result;

        try
        {
            result = await inner.HandleAsync(command, cancellationToken);
        }
        catch (Exception exception)
        {
            // An attempt that threw is still an attempt (NFR-09). The hash gate's refusal is the one
            // that matters most: PasswordHashSaturationFilter turns it into a 503 outside this
            // decorator, so without an entry here a shed login leaves nothing and the
            // CREDENTIAL_VERIFICATION_SHEDDING signal, which counts entries with that reason, never
            // fires. Any other exception records one fixed reason — its own message can carry
            // provider text or caller input — and is rethrown unchanged for whatever handles it.
            await TryWriteAsync(
                action, null, false,
                exception is PasswordHashGateSaturatedException
                    ? AuthMessages.AuthenticationTemporarilyUnavailable
                    : AuditMessages.CommandFailed);

            throw;
        }

        var targetId = ResolveTargetId(result.Data);

        // Refusals are recorded too, and they are usually the entries worth having: a caller
        // repeatedly denied a scope they do not own, or repeatedly failing a password, leaves no
        // other trace anywhere. Only the first error is kept — enough to say why, and all of them
        // are the application's own canonical messages, so nothing a caller submitted is written
        // here.
        await TryWriteAsync(
            action, targetId, result.Success, result.Success ? null : result.Errors.FirstOrDefault());

        // A side effect only happened if the command did, so a refusal records none.
        if (result.Success && result.Data is IAuditedSideEffects sideEffects)
        {
            foreach (var sideEffect in sideEffects.AuditedSideEffects())
            {
                await TryWriteAsync(sideEffect, targetId, true, null);
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
