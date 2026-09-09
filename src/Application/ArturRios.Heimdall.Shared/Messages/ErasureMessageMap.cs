using ArturRios.Util.Http;

namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Maps each <see cref="ErasureMessages" /> value to its HTTP status code, following UC-42's
///     flows. Passed to the response resolver.
/// </summary>
public static class ErasureMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes =
        DataAccessMessageMap.CombinedWith(new Dictionary<string, int>
        {
            // UC-42 main flow — request recorded, identity suspended.
            [ErasureMessages.ErasureRequested] = HttpStatusCodes.Ok,
            // AF-42d — recorded and running, but blocked. Still a success: the request was accepted,
            // and answering 4xx would tell the subject their right had been refused when it has not.
            [ErasureMessages.ErasureRequestedButBlocked] = HttpStatusCodes.Ok,
            // AF-42a — the re-authentication credential failed.
            [ErasureMessages.CredentialNotAccepted] = HttpStatusCodes.Unauthorized,
            // AF-42b — the token names no identity this endpoint can act for.
            [ErasureMessages.NotEligible] = HttpStatusCodes.Forbidden,
            // AF-42c — already requested.
            [ErasureMessages.ErasureAlreadyRequested] = HttpStatusCodes.Conflict,
            // UC-41 — the copy was produced.
            [ErasureMessages.DataExported] = HttpStatusCodes.Ok,
            // UC-43 read.
            [ErasureMessages.ErasureRequestsRetrieved] = HttpStatusCodes.Ok
        });
}
