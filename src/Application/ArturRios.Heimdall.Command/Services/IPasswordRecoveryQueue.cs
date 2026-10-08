namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     What UC-12 hands to the background: the address and scope a caller asked about, exactly as
///     they asked. Nothing about the answer — whether anybody holds the address — is known yet.
/// </summary>
public sealed record PasswordRecoveryRequest(string Email, Guid? ScopeId);

/// <summary>
///     Takes UC-12's work off the request path (AF-12a). <see cref="PasswordRecoveryProcessor" />
///     does it later, outside any request, so the response cannot be timed to learn whether the
///     address is registered.
/// </summary>
public interface IPasswordRecoveryQueue
{
    /// <summary>
    ///     Queues the request without waiting. Returns <c>false</c> when the queue is full and the
    ///     request was dropped; the caller's answer must not change either way.
    /// </summary>
    bool TryEnqueue(PasswordRecoveryRequest request);
}
