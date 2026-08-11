using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.RemoveScopeOwnerCommand" /> (UC-22). Reports the two
///     <c>PublicId</c>s the removed ownership linked. The <c>SCOPE_OWNER</c> row itself had no
///     identifier to return — it is a join row, not an addressable resource — and internal Ids never
///     leave the data layer. There is no idempotent path to flag: a repeated call finds no ownership
///     row and answers AF-22a's 404.
/// </summary>
public class RemoveScopeOwnerCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the scope that lost the owner.</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the person who no longer owns the scope.</summary>
    public Guid PersonId { get; set; }
}
