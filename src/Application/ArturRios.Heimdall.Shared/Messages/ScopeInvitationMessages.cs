namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Messages of a Scope Admin's UC-21 (an invitation instead of an ownership, FR-SC-14) and of UC-58…UC-61:
///     withdrawing, listing, accepting and declining scope owner invitations.
/// </summary>
public static class ScopeInvitationMessages
{
    /// <summary>UC-21 for a Scope Admin actor — the invitation is open, newly or already.</summary>
    public const string InvitationSent = "The invitation was sent.";

    /// <summary>
    ///     AF-58c, AF-60a, AF-61a — unknown, ended, expired, somebody else's, or voided at acceptance (AF-60b).
    ///     One answer for all of them, so the endpoints say nothing about invitations a caller cannot act on.
    /// </summary>
    public const string InvitationNotOpen = "The invitation was not found or is no longer open.";

    /// <summary>UC-60 main flow.</summary>
    public const string InvitationAccepted = "The invitation was accepted.";

    /// <summary>UC-61 main flow.</summary>
    public const string InvitationDeclined = "The invitation was declined.";
}
