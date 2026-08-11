using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     The person as it stands after a UC-23 promotion — the use case returns the updated person
///     rather than the join rows it moved. Exposes only external-facing identifiers and has no field
///     for <c>PasswordHash</c> or <c>Salt</c>. There is no <c>ScopeId</c> field: a promoted person no
///     longer belongs to any scope as a <c>User</c>, so it would be <c>null</c> on every response —
///     the scope they now own appears in <see cref="OwnedScopeIds" />.
/// </summary>
public class PromoteScopeUserCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the promoted person.</summary>
    public Guid Id { get; set; }

    /// <summary>Full name, unchanged by the promotion.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Email address, unchanged by the promotion.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Role value after the promotion — always <c>ScopeAdmin</c> (see <c>Roles</c>).</summary>
    public int Role { get; set; }

    /// <summary>Whether the email is verified; carried over untouched, as UC-23 names no re-verification.</summary>
    public bool EmailVerified { get; set; }

    /// <summary>Public identifiers of the scopes the person owns, now including the promoting scope.</summary>
    public IEnumerable<Guid> OwnedScopeIds { get; set; } = new List<Guid>();

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Update timestamp, stamped by this operation.</summary>
    public DateTime UpdatedAt { get; set; }
}
