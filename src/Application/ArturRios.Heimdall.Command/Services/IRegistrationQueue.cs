namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     What UC-47 hands to the background: who asked to be registered, where, and the password already
///     hashed. Whether the address is free is not known yet. The plaintext password never gets here.
/// </summary>
public sealed record RegistrationRequest(
    Guid ScopeId,
    string Name,
    string Email,
    byte[] PasswordHash,
    byte[] Salt);

/// <summary>
///     Takes UC-47's address-dependent work off the request path (AF-47b), as
///     <see cref="IPasswordRecoveryQueue" /> does for UC-12. <see cref="RegistrationProcessor" /> does it
///     later, so the response cannot be timed to learn whether the address is registered.
/// </summary>
public interface IRegistrationQueue
{
    /// <summary>
    ///     Queues the request without waiting. Returns <c>false</c> when the queue is full and the
    ///     request was dropped; the caller's answer must not change either way.
    /// </summary>
    bool TryEnqueue(RegistrationRequest request);
}
