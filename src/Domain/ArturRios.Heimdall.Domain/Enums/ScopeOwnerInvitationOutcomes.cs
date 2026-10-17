using System.ComponentModel;

namespace ArturRios.Heimdall.Domain.Enums;

/// <summary>
///     How a scope owner invitation ended (UC-58…UC-61), stored as <c>smallint</c>. An invitation that
///     ran out unanswered has no outcome: it ended by expiry.
/// </summary>
public enum ScopeOwnerInvitationOutcomes : short
{
    [Description("The invitee accepted and now owns the scope")]
    Accepted = 1,

    [Description("The invitee declined")]
    Declined = 2,

    [Description("An owner of the scope, or a System Admin, withdrew it")]
    Withdrawn = 3,

    [Description("It could no longer be honoured: the scope was deleted or the invitee is no longer eligible")]
    Voided = 4
}
