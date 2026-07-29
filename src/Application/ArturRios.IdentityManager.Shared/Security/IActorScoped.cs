namespace ArturRios.IdentityManager.Shared.Security;

/// <summary>
///     A command or query whose authorization depends on the acting caller. The controller populates
///     these fields from the authenticated user (never from the request) so the handler can enforce
///     scope-scoped rules such as UC-06 AF-06e and UC-07 AF-07b.
/// </summary>
public interface IActorScoped
{
    /// <summary>The acting caller's internal person id.</summary>
    long ActingPersonId { get; set; }

    /// <summary>The acting caller's role value (see <c>Roles</c>).</summary>
    int ActingRole { get; set; }
}
