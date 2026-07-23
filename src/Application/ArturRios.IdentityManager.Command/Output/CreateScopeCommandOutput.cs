using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Output;

/// <summary>
///     The scope created by <see cref="Input.CreateScopeCommand" /> (UC-01). Only externally-facing
///     <c>PublicId</c> identifiers are exposed; internal Ids never leave the data layer.
/// </summary>
public class CreateScopeCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the created scope.</summary>
    public Guid Id { get; set; }

    /// <summary>Scope display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Scope description, if any.</summary>
    public string? Description { get; set; }

    /// <summary>Whether Google sign-in is enabled for the scope (defaults to <c>false</c> on creation).</summary>
    public bool GoogleSignInEnabled { get; set; }

    /// <summary>Public identifiers of the persons designated as the scope's initial owners.</summary>
    public IEnumerable<Guid> OwnerIds { get; set; } = new List<Guid>();

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }
}
