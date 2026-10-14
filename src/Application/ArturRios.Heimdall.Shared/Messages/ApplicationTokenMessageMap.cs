using ArturRios.Util.Http;

namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>Status codes for <c>ApplicationTokenController</c>'s messages (UC-56, UC-57).</summary>
public static class ApplicationTokenMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes =
        DataAccessMessageMap.CombinedWith(new Dictionary<string, int>
        {
            // UC-56 main flow — token issued.
            [ApplicationTokenMessages.TokenIssued] = HttpStatusCodes.Ok,
            // AF-56a — a field is missing or the secret is over-long.
            [ApplicationTokenMessages.ApplicationIdRequired] = HttpStatusCodes.BadRequest,
            [ApplicationTokenMessages.ScopeIdRequired] = HttpStatusCodes.BadRequest,
            [ApplicationTokenMessages.SecretRequired] = HttpStatusCodes.BadRequest,
            [ApplicationTokenMessages.SecretTooLong] = HttpStatusCodes.BadRequest,
            // AF-56b — any refusal: unknown, revoked or expired secret, another application's, a
            // deleted application or scope, or the wrong scope. One status, one message.
            [ApplicationTokenMessages.CredentialsNotAccepted] = HttpStatusCodes.Unauthorized
        });
}
