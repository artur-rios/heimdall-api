namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     A command whose authorization depends on the acting caller. The controller populates these
///     fields from the authenticated user (never from the request body) so the handler can enforce
///     scope-scoped rules such as UC-06 AF-06e.
/// </summary>
public interface IActorScopedCommand
{
    /// <summary>The acting caller's internal person id.</summary>
    long ActingPersonId { get; set; }

    /// <summary>The acting caller's role value (see <c>Roles</c>).</summary>
    int ActingRole { get; set; }
}
