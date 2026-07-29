using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Output;

/// <summary>
///     The person created by any UC-06 path. Exposes only external-facing identifiers; never
///     <c>PasswordHash</c> / <c>Salt</c>. <see cref="ScopeId" /> is populated for paths a and c and
///     <c>null</c> for path b (admins have no scope association at creation).
/// </summary>
public class CreatePersonCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the created person.</summary>
    public Guid Id { get; set; }

    /// <summary>Full name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Email address.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Assigned role value (see <c>Roles</c>).</summary>
    public int Role { get; set; }

    /// <summary>Whether the email is verified (always <c>false</c> at creation).</summary>
    public bool EmailVerified { get; set; }

    /// <summary>Public identifier of the associated scope (paths a and c); <c>null</c> for path b.</summary>
    public Guid? ScopeId { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }
}
