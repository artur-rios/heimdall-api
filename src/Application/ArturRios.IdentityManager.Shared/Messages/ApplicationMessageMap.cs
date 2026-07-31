using ArturRios.Util.Http;

namespace ArturRios.IdentityManager.Shared.Messages;

/// <summary>
///     Maps each <see cref="ApplicationMessages" /> value to its HTTP status code, following the UC-16
///     flows. Passed to the response resolver.
/// </summary>
public static class ApplicationMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        // UC-16 main flow — application created.
        [ApplicationMessages.ApplicationCreatedSuccessfully] = HttpStatusCodes.Created,
        // AF-16a — scope not found.
        [ApplicationMessages.ScopeNotFound] = HttpStatusCodes.NotFound,
        // UC-16 — the acting Scope Admin does not own the target scope; AF-16c — a User named
        // someone else as owner.
        [ApplicationMessages.NotScopeOwner] = HttpStatusCodes.Forbidden,
        [ApplicationMessages.CannotSetAnotherOwner] = HttpStatusCodes.Forbidden,
        // AF-16b — the owner is not tied to the scope.
        [ApplicationMessages.OwnerNotValidForScope] = HttpStatusCodes.BadRequest,
        // AF-16d — invalid input.
        [ApplicationMessages.NameRequired] = HttpStatusCodes.BadRequest,
        [ApplicationMessages.NameTooLong] = HttpStatusCodes.BadRequest,
        [ApplicationMessages.OwnerRequired] = HttpStatusCodes.BadRequest
    };
}
