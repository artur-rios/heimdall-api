using ArturRios.Util.Http;

namespace ArturRios.IdentityManager.Shared.Messages;

/// <summary>
///     Maps each <see cref="AuthMessages" /> value to its HTTP status code, following the UC-11,
///     UC-12, and UC-13 flows. Passed to the response resolver.
/// </summary>
public static class AuthMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        // UC-11 main flow — authenticated, token issued.
        [AuthMessages.LoginSuccessful] = HttpStatusCodes.Ok,
        // AF-11a…AF-11e — every rejection answers alike, so none of them is distinguishable.
        [AuthMessages.InvalidCredentials] = HttpStatusCodes.Unauthorized,
        // AF-11f — malformed request. UC-12's validator reuses the two email messages.
        [AuthMessages.EmailRequired] = HttpStatusCodes.BadRequest,
        [AuthMessages.EmailInvalid] = HttpStatusCodes.BadRequest,
        [AuthMessages.PasswordRequired] = HttpStatusCodes.BadRequest,
        // UC-12 main flow and AF-12a — both answer 200 with the same message.
        [AuthMessages.PasswordRecoveryRequested] = HttpStatusCodes.Ok,
        // UC-13 main flow — the password was changed.
        [AuthMessages.PasswordResetSuccessful] = HttpStatusCodes.Ok,
        // AF-13a…AF-13c — each token rejection is named, and all three are bad requests.
        [AuthMessages.ResetTokenInvalid] = HttpStatusCodes.BadRequest,
        [AuthMessages.ResetTokenExpired] = HttpStatusCodes.BadRequest,
        [AuthMessages.ResetTokenAlreadyUsed] = HttpStatusCodes.BadRequest,
        // AF-13d — malformed request. The password messages are shared with AF-11f.
        [AuthMessages.TokenRequired] = HttpStatusCodes.BadRequest,
        [AuthMessages.PasswordTooShort] = HttpStatusCodes.BadRequest
    };
}
