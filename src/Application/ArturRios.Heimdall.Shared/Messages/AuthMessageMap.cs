using ArturRios.Util.Http;

namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Maps each <see cref="AuthMessages" /> value to its HTTP status code, following the UC-11,
///     UC-12, UC-13, UC-14, UC-15, UC-25, and UC-26 flows. Passed to the response resolver.
/// </summary>
public static class AuthMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        // UC-11 main flow — authenticated, token issued.
        [AuthMessages.LoginSuccessful] = HttpStatusCodes.Ok,
        // AF-11g — password check passed, a UC-38 challenge token was issued instead.
        [AuthMessages.TwoFactorRequired] = HttpStatusCodes.Ok,
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
        // UC-14 main flow — the address was confirmed.
        [AuthMessages.EmailVerifiedSuccessfully] = HttpStatusCodes.Ok,
        // UC-15 main flow — a fresh verification link was issued and mailed.
        [AuthMessages.VerificationEmailSent] = HttpStatusCodes.Ok,
        // AF-15a — the address is already verified, so no link is worth sending.
        [AuthMessages.EmailAlreadyVerified] = HttpStatusCodes.BadRequest,
        // UC-15 — the caller's own token names a person who no longer exists (ClaimsOnly outlives a
        // hard deletion). The same answer UC-07 AF-07a gives for the same fact.
        [AuthMessages.PersonNotFound] = HttpStatusCodes.NotFound,
        // AF-13a…AF-13c, and AF-14a…AF-14c — each token rejection is named, and all of them are bad
        // requests. The two use cases specify the same three messages, so they share these entries.
        [AuthMessages.TokenInvalid] = HttpStatusCodes.BadRequest,
        [AuthMessages.TokenExpired] = HttpStatusCodes.BadRequest,
        [AuthMessages.TokenAlreadyUsed] = HttpStatusCodes.BadRequest,
        // AF-13d, and UC-14's input validation — malformed request. The password messages are shared
        // with AF-11f.
        [AuthMessages.TokenRequired] = HttpStatusCodes.BadRequest,
        [AuthMessages.PasswordTooShort] = HttpStatusCodes.BadRequest,
        // UC-25 main flow — the Google account was signed up or signed in and a token issued.
        [AuthMessages.GoogleSignInSuccessful] = HttpStatusCodes.Ok,
        // AF-25a and AF-25d — an unverifiable token and a deleted Google User answer alike, as
        // UC-11's rejections do. Shared with UC-26 AF-26a, whose refusals are the same fact.
        [AuthMessages.GoogleAuthenticationFailed] = HttpStatusCodes.Unauthorized,
        // UC-26 main flow — the Google session ended and the client should drop the token.
        [AuthMessages.GoogleSignOutSuccessful] = HttpStatusCodes.Ok,
        // AF-25b — the scope is missing, deleted, or has the setting off. 403 rather than 404: the
        // use case refuses the operation without saying which of the three is the reason.
        [AuthMessages.GoogleSignInUnavailable] = HttpStatusCodes.Forbidden,
        // AF-25c — the verified address is already taken within the scope.
        [AuthMessages.EmailAlreadyExists] = HttpStatusCodes.Conflict
    };
}
