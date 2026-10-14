using ArturRios.Util.Http;

namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Maps each message the audit-trail listing (UC-48) can return to its HTTP status code. Passed to
///     the response resolver by <c>AuditController</c>.
/// </summary>
/// <remarks>
///     AF-48b (a caller who is not a System Admin) has no entry: the endpoint's <c>RoleRequirement</c>
///     answers 403 before the handler runs.
/// </remarks>
public static class AuditMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes =
        DataAccessMessageMap.CombinedWith(new Dictionary<string, int>
        {
            // UC-48 main flow and AF-48c — entries listed, an empty page included.
            [AuditMessages.AuditLogRetrieved] = HttpStatusCodes.Ok,
            // AF-48a — the date range runs backwards.
            [AuditMessages.InvalidDateRange] = HttpStatusCodes.BadRequest,
            // AF-48a (NFR-10) — the page is out of bounds or the action filter is longer than its column.
            [PaginationMessages.InvalidPageNumber] = HttpStatusCodes.BadRequest,
            [PaginationMessages.InvalidPageSize] = HttpStatusCodes.BadRequest,
            [PaginationMessages.FilterTooLong] = HttpStatusCodes.BadRequest
        });
}
