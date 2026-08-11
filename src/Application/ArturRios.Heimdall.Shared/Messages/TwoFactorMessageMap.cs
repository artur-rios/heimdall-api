using ArturRios.Util.Http;

namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Maps each <see cref="TwoFactorMessages" /> value to its HTTP status code, following UC-36's
///     and UC-37's flows. Passed to the response resolver.
/// </summary>
public static class TwoFactorMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        // UC-36 main flow and AF-36d — setup initiated or a pending setup overwritten.
        [TwoFactorMessages.SetupInitiated] = HttpStatusCodes.Ok,
        // AF-36a and AF-37d — already active.
        [TwoFactorMessages.AlreadyActive] = HttpStatusCodes.Conflict,
        // AF-36b — caller is not eligible (Google User, or no live person named by the token).
        [TwoFactorMessages.NotEligible] = HttpStatusCodes.Forbidden,
        // AF-36c — neither method selected.
        [TwoFactorMessages.NoMethodSelected] = HttpStatusCodes.BadRequest,
        // UC-37 main flow — setup confirmed, recovery codes issued.
        [TwoFactorMessages.SetupConfirmed] = HttpStatusCodes.Ok,
        // AF-37a — no pending setup exists for the caller.
        [TwoFactorMessages.NoPendingSetup] = HttpStatusCodes.NotFound,
        // AF-37b — appCode missing or incorrect.
        [TwoFactorMessages.AppCodeInvalid] = HttpStatusCodes.BadRequest,
        // AF-37c — emailCode missing, incorrect, expired, or already used.
        [TwoFactorMessages.EmailCodeInvalid] = HttpStatusCodes.BadRequest,
        // UC-38 main flow — challenge redeemed, full token issued.
        [TwoFactorMessages.VerificationSuccessful] = HttpStatusCodes.Ok,
        // AF-38a — challenge token missing, invalid, or expired.
        [TwoFactorMessages.ChallengeTokenInvalid] = HttpStatusCodes.Unauthorized,
        // AF-38b and AF-38c — collapsed to the same 401 so a wrong code and a reused recovery code
        // cannot be told apart.
        [TwoFactorMessages.FactorInvalid] = HttpStatusCodes.Unauthorized
    };
}
