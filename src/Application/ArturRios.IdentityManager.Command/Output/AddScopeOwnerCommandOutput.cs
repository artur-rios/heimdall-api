using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Output;

/// <summary>
///     Result of <see cref="Input.AddScopeOwnerCommand" /> (UC-21). Reports the two <c>PublicId</c>s
///     the new ownership links and whether the request was the idempotent no-op of AF-21d. The
///     <c>SCOPE_OWNER</c> row itself has no identifier to return — it is a join row, not an
///     addressable resource — and internal Ids never leave the data layer.
/// </summary>
public class AddScopeOwnerCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the scope that gained the owner.</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the person now owning the scope.</summary>
    public Guid PersonId { get; set; }

    /// <summary>
    ///     <c>true</c> when the person already owned the scope and nothing was written (AF-21d);
    ///     <c>false</c> when this request created the ownership. AF-21d already answers with its own
    ///     status (200 against the main flow's 201), so this flag confirms which path ran rather than
    ///     being the only way to tell them apart.
    /// </summary>
    public bool AlreadyOwner { get; set; }
}
