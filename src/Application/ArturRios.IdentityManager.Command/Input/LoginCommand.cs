using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to authenticate a person and obtain a token (UC-11). <see cref="ScopeId" /> selects
///     which lookup applies: present, the person is sought among the <c>User</c>s of that scope,
///     whose emails are only unique within it (FR-AU-01); absent, among the <c>ScopeAdmin</c>s and
///     <c>SystemAdmin</c>s, whose emails are unique system-wide (FR-AU-02).
/// </summary>
public class LoginCommand : BaseCommand
{
    /// <summary>The email to authenticate with.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>The password to authenticate with.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    ///     The <c>PublicId</c> of the scope the person belongs to. Required for a <c>User</c>,
    ///     omitted by a <c>ScopeAdmin</c> or <c>SystemAdmin</c>.
    /// </summary>
    public Guid? ScopeId { get; set; }
}
