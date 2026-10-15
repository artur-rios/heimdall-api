using ArturRios.Util.Http;

namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Maps each <see cref="TwoFactorMessages" /> value to its HTTP status code, following UC-36's
///     and UC-37's flows. Passed to the response resolver.
/// </summary>
public static class TwoFactorMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes =
        DataAccessMessageMap.CombinedWith(new Dictionary<string, int>
        {
            // UC-36 main flow and AF-36d — setup initiated or a pending setup overwritten.
            [TwoFactorMessages.SetupInitiated] = HttpStatusCodes.Ok,
            // AF-36a and AF-37d — already active.
            [TwoFactorMessages.AlreadyActive] = HttpStatusCodes.Conflict,
            // AF-36b — caller is not eligible (Google User, or no live person named by the token).
            [TwoFactorMessages.NotEligible] = HttpStatusCodes.Forbidden,
            // AF-36c — neither method selected.
            [TwoFactorMessages.NoMethodSelected] = HttpStatusCodes.BadRequest,
            // AF-36f — a setup code went out within the per-person cooldown (FR-2F-19), as AF-15b.
            [AuthMessages.EmailSentRecently] = HttpStatusCodes.TooManyRequests,
            // UC-37 main flow — setup confirmed, recovery codes issued.
            [TwoFactorMessages.SetupConfirmed] = HttpStatusCodes.Ok,
            // AF-37a — no pending setup exists for the caller.
            [TwoFactorMessages.NoPendingSetup] = HttpStatusCodes.NotFound,
            // AF-36e, AF-37b, AF-37c, AF-39b, AF-39c and AF-40b (FR-AU-13) — a password or code checked
            // under a bearer token was wrong, missing or already used, or the account's
            // re-authentication budget is paused: one message, and the status the password refusal
            // has always had.
            [AuthMessages.PasswordOrCodeIncorrect] = HttpStatusCodes.Unauthorized,
            // UC-38 main flow — challenge redeemed, full token issued.
            [TwoFactorMessages.VerificationSuccessful] = HttpStatusCodes.Ok,
            // UC-38 for a restricted person (NFR-24) — a subject-rights-only token.
            [AuthMessages.SubjectRightsOnlySignIn] = HttpStatusCodes.Ok,
            // AF-38a — challenge token missing, invalid, or expired.
            [TwoFactorMessages.ChallengeTokenInvalid] = HttpStatusCodes.Unauthorized,
            // UC-46 — the one answer every flow gives, main and alternative alike (FR-2F-16).
            [TwoFactorMessages.ChallengeCodeResent] = HttpStatusCodes.Ok,
            // UC-38 step 5 — the second factor checked out but scope eligibility no longer holds.
            [TwoFactorMessages.ScopeNoLongerEligible] = HttpStatusCodes.Unauthorized,
            // AF-38b and AF-38c — collapsed to the same 401 so a wrong code, a reused recovery code and
            // a locked second factor (FR-AU-12) cannot be told apart.
            [TwoFactorMessages.FactorInvalid] = HttpStatusCodes.Unauthorized,
            // UC-39 main flow — the configuration and its recovery codes were removed.
            [TwoFactorMessages.Disabled] = HttpStatusCodes.Ok,
            // AF-39a — two-factor authentication is not active for the caller. Also UC-40's AF-40a: the
            // same "not active" refusal, reused rather than duplicated.
            [TwoFactorMessages.NotActive] = HttpStatusCodes.NotFound,
            // UC-40 main flow — every existing recovery code was replaced with ten new ones.
            [TwoFactorMessages.RecoveryCodesRegenerated] = HttpStatusCodes.Ok,
            // FR-2F-15 main flow — status read, including the all-false "never enabled" state.
            [TwoFactorMessages.StatusRetrieved] = HttpStatusCodes.Ok
        });
}
