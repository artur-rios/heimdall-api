namespace ArturRios.IdentityManager.Shared.Messages;

/// <summary>
///     Canonical messages produced by the Google User use cases (UC-27 – UC-29). Each is mapped to an
///     HTTP status code in <see cref="GoogleUserMessageMap" />.
/// </summary>
/// <remarks>
///     Separate from <see cref="AuthMessages" />, which carries UC-25's and UC-26's: those are
///     authentication answers given to the Google User themselves, these are answers about a Google
///     User given to somebody administering them. The split also keeps the anti-enumeration wording
///     of the authentication flows away from the plainly-named administrative ones.
/// </remarks>
public static class GoogleUserMessages
{
    /// <summary>UC-27 success: a single Google User was retrieved.</summary>
    public const string GoogleUserRetrievedSuccessfully = "Google user retrieved successfully.";

    /// <summary>UC-27 success: a list of Google Users was retrieved.</summary>
    public const string GoogleUsersRetrievedSuccessfully = "Google users retrieved successfully.";

    /// <summary>
    ///     AF-27a: no Google User holds that identifier inside the addressed scope — or it is
    ///     logically deleted and was not explicitly requested (FR-GO-17).
    /// </summary>
    public const string GoogleUserNotFound = "Google user not found.";

    /// <summary>
    ///     AF-27a, on the listing: the addressed scope does not exist or is logically deleted. Named
    ///     apart from <see cref="GoogleUserNotFound" /> because it is a different resource that is
    ///     missing, as UC-16's and UC-17's listings distinguish them.
    /// </summary>
    public const string ScopeNotFound = "Scope not found.";

    /// <summary>AF-27b: the caller is not allowed to view the requested Google User.</summary>
    public const string NotAuthorizedToViewGoogleUser = "You are not allowed to view this google user.";

    /// <summary>
    ///     AF-27b, on the listing: a Scope Admin asked for the Google Users of a scope they do not
    ///     own. Named apart from <see cref="NotAuthorizedToViewGoogleUser" /> for the reason the two
    ///     404s are: the caller failed to reach a scope, not a Google User.
    /// </summary>
    public const string NotScopeOwner = "You are not an owner of the target scope.";
}
