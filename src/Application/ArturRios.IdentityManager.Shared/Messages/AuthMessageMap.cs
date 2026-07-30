using ArturRios.Util.Http;

namespace ArturRios.IdentityManager.Shared.Messages;

/// <summary>
///     Maps each <see cref="AuthMessages" /> value to its HTTP status code, following the UC-11
///     flows. Passed to the response resolver.
/// </summary>
public static class AuthMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        // UC-11 main flow — authenticated, token issued.
        [AuthMessages.LoginSuccessful] = HttpStatusCodes.Ok,
        // AF-11a…AF-11e — every rejection answers alike, so none of them is distinguishable.
        [AuthMessages.InvalidCredentials] = HttpStatusCodes.Unauthorized,
        // AF-11f — malformed request.
        [AuthMessages.EmailRequired] = HttpStatusCodes.BadRequest,
        [AuthMessages.EmailInvalid] = HttpStatusCodes.BadRequest,
        [AuthMessages.PasswordRequired] = HttpStatusCodes.BadRequest
    };
}
