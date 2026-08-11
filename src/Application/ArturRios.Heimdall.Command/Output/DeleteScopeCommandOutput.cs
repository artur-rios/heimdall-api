using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.DeleteScopeCommand" /> (UC-04). Reports the deleted scope's
///     <c>PublicId</c> and the totals of its Users, Google Users, and applications — counted
///     regardless of their individual deletion state. Internal Ids never leave the data layer.
/// </summary>
public class DeleteScopeCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the deleted scope.</summary>
    public Guid Id { get; set; }

    /// <summary>
    ///     Total number of Users (via SCOPE_USER) belonging to the scope. A total, not a count of
    ///     what this call deleted: on the AF-04b idempotent path nothing is written and this is
    ///     still reported.
    /// </summary>
    public int UserCount { get; set; }

    /// <summary>Total number of Google Users belonging to the scope.</summary>
    public int GoogleUserCount { get; set; }

    /// <summary>Total number of applications belonging to the scope.</summary>
    public int ApplicationCount { get; set; }
}
