using ArturRios.Util.Http;

namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>Status codes for <c>ApplicationCredentialController</c>'s messages (UC-53…UC-55).</summary>
public static class ApplicationCredentialMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes =
        DataAccessMessageMap.CombinedWith(new Dictionary<string, int>
        {
            // UC-53 main flow — credential issued.
            [ApplicationCredentialMessages.CredentialIssued] = HttpStatusCodes.Created,
            // AF-53a, AF-54a, AF-55a — no live application in the addressed, live scope.
            [ApplicationMessages.ApplicationNotFound] = HttpStatusCodes.NotFound,
            // AF-53b, AF-54b, AF-55b — the actor may not manage the application.
            [ApplicationCredentialMessages.NotAuthorizedToManageCredentials] = HttpStatusCodes.Forbidden,
            // AF-53c — the expiry is not in the future.
            [ApplicationCredentialMessages.ExpiryNotInFuture] = HttpStatusCodes.BadRequest,
            // AF-53c — a body that is not JSON, or an expiry that is not a date-time.
            [ApplicationCredentialMessages.RequestMalformed] = HttpStatusCodes.BadRequest,
            // AF-53d — two live credentials already.
            [ApplicationCredentialMessages.CredentialLimitReached] = HttpStatusCodes.Conflict,
            // UC-54 main flow and its empty case — credentials listed.
            [ApplicationCredentialMessages.CredentialsRetrieved] = HttpStatusCodes.Ok,
            // UC-55 main flow — credential revoked.
            [ApplicationCredentialMessages.CredentialRevoked] = HttpStatusCodes.Ok,
            // AF-55c — no such credential on this application.
            [ApplicationCredentialMessages.CredentialNotFound] = HttpStatusCodes.NotFound,
            // AF-55d — already revoked.
            [ApplicationCredentialMessages.CredentialAlreadyRevoked] = HttpStatusCodes.Conflict
        });
}
