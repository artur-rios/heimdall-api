using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.HardDeleteScopeCommand" /> (UC-05). Reports the removed scope's
///     <c>PublicId</c> and the totals of its Users, Google Users, applications, and scope
///     permissions — counted regardless of their individual deletion state. Internal Ids never
///     leave the data layer.
/// </summary>
public class HardDeleteScopeCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the hard-deleted scope.</summary>
    public Guid Id { get; set; }

    /// <summary>Total number of Users (via SCOPE_USER) that belonged to the scope.</summary>
    public int UserCount { get; set; }

    /// <summary>Total number of Google Users that belonged to the scope.</summary>
    public int GoogleUserCount { get; set; }

    /// <summary>Total number of applications that belonged to the scope.</summary>
    public int ApplicationCount { get; set; }

    /// <summary>
    ///     Total number of scope permissions that belonged to the scope, removed via
    ///     <c>ON DELETE CASCADE</c> rather than explicitly by this handler.
    /// </summary>
    public int ScopePermissionCount { get; set; }
}
