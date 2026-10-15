using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to update a person's name, email, and — for a System Admin — role (UC-08). The person
///     is addressed by <see cref="Id" />, assigned from the route. PUT semantics: <see cref="Name" />
///     and <see cref="Email" /> are replaced. <see cref="RoleId" /> is optional; <c>null</c> leaves
///     the role unchanged, which is what every non-System-Admin caller sends. A caller changing their
///     own email also proves they are the account holder with <see cref="CurrentPassword" />, and
///     <see cref="TwoFactorCode" /> when their second factor is active (FR-PE-14).
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller and are never bound from the body. All three are <c>[JsonIgnore]</c>, so
///     they are not deserialized from the body and do not appear in the request schema.
/// </summary>
public class UpdatePersonCommand : BaseCommand, IActorScoped
{
    /// <summary>Public identifier of the person to update (assigned from the route).</summary>
    [JsonIgnore]
    public Guid Id { get; set; }

    /// <summary>New full name. Required, max 200 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>New email address. Required; changing it clears <c>EmailVerified</c> (UC-08 step 4).</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>New role value (see <c>Roles</c>), or <c>null</c> to leave the role unchanged.</summary>
    public int? RoleId { get; set; }

    /// <summary>
    ///     The caller's current password. Required only when the caller changes their own email
    ///     (FR-PE-14); ignored otherwise, so an administrator updating someone else sends none.
    /// </summary>
    public string? CurrentPassword { get; set; }

    /// <summary>
    ///     A current second factor — an authenticator-app or email code, or an unused recovery code —
    ///     required beside <see cref="CurrentPassword" /> when the caller changes their own email and
    ///     their two-factor authentication is active (FR-PE-14); ignored otherwise.
    /// </summary>
    public string? TwoFactorCode { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
