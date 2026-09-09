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
    /// <summary>UC-44: processing of the caller's identity is now restricted.</summary>
    public const string ProcessingRestricted =
        "Processing of your data is restricted. Your account is suspended until it is lifted.";

    /// <summary>UC-44 AF-44a: already restricted.</summary>
    public const string ProcessingAlreadyRestricted = "Processing of this account is already restricted.";

    /// <summary>UC-44 AF-44b: the ground named is not one Art. 18(1) recognises.</summary>
    public const string RestrictionGroundUnknown =
        "The ground given is not one the law recognises for restricting processing.";

    /// <summary>UC-45: the restriction was lifted.</summary>
    public const string RestrictionLifted = "The restriction on processing has been lifted.";

    /// <summary>UC-45 AF-45a: nothing is restricted for that identity.</summary>
    public const string NotRestricted = "Processing of this account is not restricted.";

    /// <summary>
    ///     UC-45 AF-45b: the subject could not be told the restriction was about to be lifted.
    /// </summary>
    /// <remarks>
    ///     GDPR Art. 18(3) makes informing the subject a precondition of lifting, not a courtesy
    ///     afterwards, so a failure to deliver stops the lift rather than being swallowed the way
    ///     other delivery failures in this API are.
    /// </remarks>
    public const string RestrictionLiftNotificationFailed =
        "The restriction was not lifted: the account holder could not be notified first.";

    /// <summary>UC-43 read: the pending erasure requests were listed.</summary>
    public const string ErasureRequestsRetrieved = "Erasure requests retrieved.";
}
