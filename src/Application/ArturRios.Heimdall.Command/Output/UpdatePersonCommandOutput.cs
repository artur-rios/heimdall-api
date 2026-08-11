using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     The person as it stands after a UC-08 update. Exposes only external-facing identifiers and
///     has no field for <c>PasswordHash</c> or <c>Salt</c>.
/// </summary>
public class UpdatePersonCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the updated person.</summary>
    public Guid Id { get; set; }

    /// <summary>Full name after the update.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Email address after the update.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Role value after the update (see <c>Roles</c>).</summary>
    public int Role { get; set; }

    /// <summary>Whether the email is verified; always <c>false</c> straight after an email change.</summary>
    public bool EmailVerified { get; set; }

    /// <summary>Public identifier of the scope the person belongs to as a User; <c>null</c> otherwise.</summary>
    public Guid? ScopeId { get; set; }

    /// <summary>Public identifiers of the scopes the person owns; empty for non-owners.</summary>
    public IEnumerable<Guid> OwnedScopeIds { get; set; } = new List<Guid>();

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Update timestamp, stamped by this operation.</summary>
    public DateTime UpdatedAt { get; set; }
}
