namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Canonical messages produced by the erasure request use case (UC-42). Each is mapped to an
///     HTTP status code in <see cref="ErasureMessageMap" />.
/// </summary>
public static class ErasureMessages
{
    /// <summary>UC-42 main flow: the request was recorded and the identity suspended.</summary>
    public const string ErasureRequested =
        "Erasure requested. Your account is suspended and will be anonymised by the deadline.";

    /// <summary>
    ///     UC-42 alternative flow: the request was recorded, the deadline is running, but the
    ///     erasure cannot be carried out until an administrator clears what blocks it.
    /// </summary>
    public const string ErasureRequestedButBlocked =
        "Erasure requested. It cannot be completed yet and an administrator has been notified.";

    /// <summary>
    ///     AF-42a: the credential presented alongside the request was not accepted.
    /// </summary>
    /// <remarks>
    ///     Erasure is irreversible, so a valid session alone is not enough to trigger it — a token
    ///     left open on a shared machine must not be able to destroy the account it belongs to. The
    ///     message says only that the credential failed, the same way UC-11's does.
    /// </remarks>
    public const string CredentialNotAccepted = "The credential presented was not accepted.";

    /// <summary>
    ///     AF-42b: the token names nobody this endpoint can act for — no live person and no live
    ///     Google User. Answered alike for all of them, so the endpoint cannot be used to probe.
    /// </summary>
    public const string NotEligible = "This account cannot request erasure.";

    /// <summary>AF-42c: an erasure has already been requested for this identity.</summary>
    public const string ErasureAlreadyRequested = "An erasure has already been requested for this account.";

    /// <summary>
    ///     AF-42d: NFR-12 — the caller is the last owner of a scope, so suspending them would leave
    ///     it ownerless. Recorded as the blocking reason rather than returned as a refusal.
    /// </summary>
    public const string BlockedByLastScopeOwnership =
        "The account owns a scope that would be left without an owner; ownership must be transferred first.";

    /// <summary>UC-41: the subject's copy of their data was produced.</summary>
    public const string DataExported = "Your data has been exported.";

    /// <summary>UC-43 read: the pending erasure requests were listed.</summary>
    public const string ErasureRequestsRetrieved = "Erasure requests retrieved.";
}
