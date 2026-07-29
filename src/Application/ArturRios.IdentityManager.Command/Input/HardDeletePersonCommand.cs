using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to permanently (hard) delete a person (UC-10). The person is addressed by
///     <see cref="Id" />, bound from the route. Removing the person also permanently removes the
///     applications they own, their password reset and email verification tokens, and their
///     <c>SCOPE_USER</c>/<c>SCOPE_OWNER</c> join rows. <see cref="ActingPersonId" /> is set by the
///     controller from the authenticated caller and is never bound from the request; it exists so the
///     handler can refuse a self-deletion (AF-10c).
/// </summary>
public class HardDeletePersonCommand : BaseCommand, IActorScoped
{
    /// <summary>Public identifier of the person to hard-delete (bound from the route).</summary>
    public Guid Id { get; set; }

    public long ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
