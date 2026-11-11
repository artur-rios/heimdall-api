using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     The application created by <see cref="Input.CreateApplicationCommand" /> (UC-16). Only
///     externally-facing <c>PublicId</c> identifiers are exposed; the internal Ids that link the row
///     to its scope and owner never leave the data layer.
/// </summary>
public class CreateApplicationCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the created application.</summary>
    public Guid Id { get; init; }

    /// <summary>Application display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Public identifier of the scope the application belongs to.</summary>
    public Guid ScopeId { get; init; }

    /// <summary>Public identifier of the person that owns the application.</summary>
    public Guid OwnerId { get; init; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }
}
