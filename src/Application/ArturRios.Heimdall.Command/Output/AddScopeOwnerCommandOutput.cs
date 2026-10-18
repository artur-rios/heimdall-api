using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.AddScopeOwnerCommand" /> (UC-21). Reports the two <c>PublicId</c>s the
///     ownership links, whether the request was the idempotent no-op of AF-21d, and — for a Scope Admin
///     actor — the invitation opened instead of the ownership (FR-SC-14). The <c>SCOPE_OWNER</c> row itself
///     has no identifier to return — it is a join row, not an addressable resource — and internal Ids never
///     leave the data layer.
/// </summary>
public class AddScopeOwnerCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the scope.</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the person added or invited.</summary>
    public Guid PersonId { get; set; }

    /// <summary>
    ///     <c>true</c> when the person already owned the scope and nothing was written (AF-21d). AF-21d already
    ///     answers with its own status (200 against the main flow's 201), so this flag confirms which path ran
    ///     rather than being the only way to tell them apart.
    /// </summary>
    public bool AlreadyOwner { get; set; }

    /// <summary>
    ///     <c>true</c> when a Scope Admin asked and the person was invited rather than added (FR-SC-14): they
    ///     own nothing until they accept (UC-60). The answer is then 202. Always <c>false</c> for a System
    ///     Admin and for AF-21d.
    /// </summary>
    public bool Invited { get; set; }

    /// <summary>The open invitation's public id when <see cref="Invited" />; what UC-58 withdraws it by.</summary>
    public Guid? InvitationId { get; set; }

    /// <summary>When the open invitation stops being acceptable, when <see cref="Invited" />.</summary>
    public DateTime? InvitationExpiresAt { get; set; }
}
