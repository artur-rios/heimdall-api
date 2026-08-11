namespace ArturRios.Heimdall.Shared.Security;

/// <summary>
///     Reads the authenticated caller for audit logging (NFR-09) without the Application layer
///     depending on Presentation-layer types. Unlike <see cref="IActorScoped" />, this is resolved by
///     the infrastructure (from the request), not populated by the controller onto a command.
/// </summary>
public interface IActorAccessor
{
    /// <summary>The acting caller's person <c>PublicId</c>; <c>null</c> on an anonymous request.</summary>
    Guid? ActorPersonId { get; }

    /// <summary>The acting caller's role value (see <c>Roles</c>); <c>null</c> on an anonymous request.</summary>
    int? ActorRole { get; }
}
