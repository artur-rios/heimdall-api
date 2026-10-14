using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Exchanges an application's secret for an application token (UC-56, FR-AP-13). Anonymous: the
///     secret is the credential. All three fields must name one live credential of one live
///     application in one live scope.
/// </summary>
public class IssueApplicationTokenCommand : BaseCommand
{
    /// <summary>The application's <c>PublicId</c>.</summary>
    public Guid ApplicationId { get; set; }

    /// <summary>The <c>PublicId</c> of the application's scope.</summary>
    public Guid ScopeId { get; set; }

    /// <summary>A secret UC-53 issued for that application.</summary>
    public string Secret { get; set; } = string.Empty;
}
