using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to permanently (hard) delete a Google User (UC-29, FR-GO-16). The record is addressed by
///     <see cref="Id" /> within <see cref="ScopeId" />, both bound from the route. Removing it
///     cascades to nothing — a Google User owns no dependent row, and the scope its foreign key points
///     at is left intact. The command carries no acting person: UC-29's only actor is the System Admin
///     and the endpoint's role requirement settles that entirely, so the handler has no
///     data-dependent rule left to apply.
/// </summary>
public class HardDeleteGoogleUserCommand : BaseCommand
{
    /// <summary>Public identifier of the scope the Google User belongs to (bound from the route).</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the Google User to hard-delete (bound from the route).</summary>
    public Guid Id { get; set; }
}
