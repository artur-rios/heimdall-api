using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to be sent a password reset link (UC-12, FR-PR-01). <see cref="ScopeId" /> selects
///     which lookup applies, exactly as it does for <see cref="LoginCommand" />: present, the person
///     is sought among the <c>User</c>s of that scope, whose emails are only unique within it;
///     absent, among the <c>ScopeAdmin</c>s and <c>SystemAdmin</c>s, whose emails are unique
///     system-wide.
/// </summary>
public class PasswordRecoveryCommand : BaseCommand
{
    /// <summary>The email to send the reset link to.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    ///     The <c>PublicId</c> of the scope the person belongs to. Supplied by a <c>User</c>,
    ///     omitted by a <c>ScopeAdmin</c> or <c>SystemAdmin</c>.
    /// </summary>
    public Guid? ScopeId { get; set; }
}
