using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Output;

/// <summary>
///     The scope as updated by <see cref="Input.UpdateScopeCommand" /> (UC-03). Only externally-facing
///     <c>PublicId</c> identifiers are exposed; internal Ids never leave the data layer.
/// </summary>
public class UpdateScopeCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the scope.</summary>
    public Guid Id { get; set; }

    /// <summary>Scope display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Scope description, if any.</summary>
    public string? Description { get; set; }

    /// <summary>Whether Google sign-in is enabled for the scope.</summary>
    public bool GoogleSignInEnabled { get; set; }

    /// <summary>Public identifiers of the scope's owners.</summary>
    public IEnumerable<Guid> OwnerIds { get; set; } = new List<Guid>();

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last update timestamp.</summary>
    public DateTime UpdatedAt { get; set; }
}
