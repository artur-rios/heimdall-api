using ArturRios.Util.Http;

namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Maps each <see cref="TwoFactorMessages" /> value to its HTTP status code, following UC-36's
///     flows. Passed to the response resolver.
/// </summary>
public static class TwoFactorMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        // UC-36 main flow and AF-36d — setup initiated or a pending setup overwritten.
        [TwoFactorMessages.SetupInitiated] = HttpStatusCodes.Ok,
        // AF-36a — already active.
        [TwoFactorMessages.AlreadyActive] = HttpStatusCodes.Conflict,
        // AF-36b — caller is not eligible (Google User, or no live person named by the token).
        [TwoFactorMessages.NotEligible] = HttpStatusCodes.Forbidden,
        // AF-36c — neither method selected.
        [TwoFactorMessages.NoMethodSelected] = HttpStatusCodes.BadRequest
    };
}
