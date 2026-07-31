using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Output;

/// <summary>
///     The application as it stands after a UC-18 update. Only externally-facing <c>PublicId</c>
///     identifiers are exposed; the internal Ids that link the row to its scope and owner never leave
///     the data layer.
/// </summary>
public class UpdateApplicationCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the updated application.</summary>
    public Guid Id { get; set; }

    /// <summary>Application display name after the update.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Public identifier of the scope the application belongs to (unchanged by this operation).</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the person that owns the application after the update.</summary>
    public Guid OwnerId { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Update timestamp, stamped by this operation.</summary>
    public DateTime UpdatedAt { get; set; }
}
