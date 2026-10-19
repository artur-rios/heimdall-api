using ArturRios.Util.Http;

namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>Status codes for <c>ScopeInvitationController</c>'s messages (UC-58…UC-61).</summary>
public static class ScopeInvitationMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes =
        DataAccessMessageMap.CombinedWith(new Dictionary<string, int>
        {
            // UC-60 and UC-61 main flows.
            [ScopeInvitationMessages.InvitationAccepted] = HttpStatusCodes.Ok,
            [ScopeInvitationMessages.InvitationDeclined] = HttpStatusCodes.Ok,
            // AF-58c, AF-60a, AF-60b, AF-61a — no open invitation the caller may act on.
            [ScopeInvitationMessages.InvitationNotOpen] = HttpStatusCodes.NotFound,
            // AF-58a — the scope is unknown or logically deleted; AF-58b — not an owner of it.
            [ScopeMessages.ScopeNotFound] = HttpStatusCodes.NotFound,
            [ScopeMessages.NotScopeOwner] = HttpStatusCodes.Forbidden,
            // UC-58 listing and UC-59 (NFR-10) — page bounds.
            [PaginationMessages.InvalidPageNumber] = HttpStatusCodes.BadRequest,
            [PaginationMessages.InvalidPageSize] = HttpStatusCodes.BadRequest
        });
}
