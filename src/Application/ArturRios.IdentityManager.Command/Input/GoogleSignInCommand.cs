using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to authenticate a Google account against a scope (UC-25, FR-GO-03…FR-GO-11) — signing
///     the account up on its first use and signing it in on every later one. Distinct from
///     <see cref="SetGoogleSignInCommand" />, which is UC-24's toggle for whether a scope allows this
///     at all.
/// </summary>
/// <remarks>
///     Carries no acting person: the endpoint is anonymous, and the only identity involved is the one
///     the Google ID token asserts. No validator either — UC-25 defines no <c>400</c> flow and needs
///     none, since an absent token fails verification (AF-25a, 401) and an empty scope identifier
///     matches no scope (AF-25b, 403). Both are outcomes the use case already names.
/// </remarks>
public class GoogleSignInCommand : BaseCommand
{
    /// <summary>Public identifier of the scope the account is signing in to (FR-GO-06).</summary>
    public Guid ScopeId { get; set; }

    /// <summary>The Google ID token the caller obtained from Google, verified before it is trusted.</summary>
    public string IdToken { get; set; } = string.Empty;
}
