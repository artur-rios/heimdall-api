using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Output;

/// <summary>
///     Result of <see cref="Input.DeleteScopeCommand" /> (UC-04). Reports the deleted scope's
///     <c>PublicId</c> and the totals of its Users, Google Users, and applications — counted
///     regardless of their individual deletion state. Internal Ids never leave the data layer.
/// </summary>
public class DeleteScopeCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the deleted scope.</summary>
    public Guid Id { get; set; }

    /// <summary>Total number of Users (via SCOPE_USER) belonging to the scope.</summary>
    public int DeletedUserCount { get; set; }

    /// <summary>Total number of Google Users belonging to the scope.</summary>
    public int DeletedGoogleUserCount { get; set; }

    /// <summary>Total number of applications belonging to the scope.</summary>
    public int DeletedApplicationCount { get; set; }
}
