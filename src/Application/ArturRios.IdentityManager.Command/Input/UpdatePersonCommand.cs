using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to update a person's name, email, and — for a System Admin — role (UC-08). The person
///     is addressed by <see cref="Id" />, bound from the route. PUT semantics: <see cref="Name" />
///     and <see cref="Email" /> are replaced. <see cref="RoleId" /> is optional; <c>null</c> leaves
///     the role unchanged, which is what every non-System-Admin caller sends.
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller and are never bound from the body.
/// </summary>
public class UpdatePersonCommand : BaseCommand, IActorScoped
{
    /// <summary>Public identifier of the person to update (bound from the route).</summary>
    public Guid Id { get; set; }

    /// <summary>New full name. Required, max 200 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>New email address. Required; changing it clears <c>EmailVerified</c> (UC-08 step 4).</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>New role value (see <c>Roles</c>), or <c>null</c> to leave the role unchanged.</summary>
    public int? RoleId { get; set; }

    public long ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
