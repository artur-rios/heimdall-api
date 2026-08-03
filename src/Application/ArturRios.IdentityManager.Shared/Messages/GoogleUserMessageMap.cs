using ArturRios.Util.Http;

namespace ArturRios.IdentityManager.Shared.Messages;

/// <summary>
///     Maps each <see cref="GoogleUserMessages" /> value to its HTTP status code, following the UC-27
///     – UC-29 flows. Passed to the response resolver.
/// </summary>
public static class GoogleUserMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        // UC-27 main flow — Google User(s) retrieved.
        [GoogleUserMessages.GoogleUserRetrievedSuccessfully] = HttpStatusCodes.Ok,
        [GoogleUserMessages.GoogleUsersRetrievedSuccessfully] = HttpStatusCodes.Ok,
        // AF-27a — no such Google User in the addressed scope, or it is logically deleted and was
        // not explicitly requested (FR-GO-17).
        [GoogleUserMessages.GoogleUserNotFound] = HttpStatusCodes.NotFound,
        // AF-27a on the listing — the scope itself is missing or logically deleted.
        [GoogleUserMessages.ScopeNotFound] = HttpStatusCodes.NotFound,
        // AF-27b — the caller may not view this Google User.
        [GoogleUserMessages.NotAuthorizedToViewGoogleUser] = HttpStatusCodes.Forbidden,
        // AF-27b on the listing — the acting Scope Admin does not own the addressed scope.
        [GoogleUserMessages.NotScopeOwner] = HttpStatusCodes.Forbidden,
        // UC-28 main flow — Google User logically deleted. AF-28b answers with the same 200, so the
        // response's AlreadyDeleted flag is what tells the two paths apart. AF-28a reuses
        // GoogleUserNotFound above.
        [GoogleUserMessages.GoogleUserDeletedSuccessfully] = HttpStatusCodes.Ok,
        // AF-28c — the caller may not delete this Google User.
        [GoogleUserMessages.NotAuthorizedToDeleteGoogleUser] = HttpStatusCodes.Forbidden,
        // UC-29 main flow — Google User hard deleted. AF-29a reuses GoogleUserNotFound above, and
        // UC-29's only other refusals are the framework's: 403 from [RoleRequirement], 401
        // unauthenticated.
        [GoogleUserMessages.GoogleUserHardDeletedSuccessfully] = HttpStatusCodes.Ok
    };
}
