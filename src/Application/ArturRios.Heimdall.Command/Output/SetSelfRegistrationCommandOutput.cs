using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>The scope as updated by <see cref="Input.SetSelfRegistrationCommand" /> (UC-47 toggle).</summary>
public class SetSelfRegistrationCommandOutput : CommandOutput
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool GoogleSignInEnabled { get; set; }

    public bool SelfRegistrationEnabled { get; set; }

    public IEnumerable<Guid> OwnerIds { get; set; } = new List<Guid>();

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
