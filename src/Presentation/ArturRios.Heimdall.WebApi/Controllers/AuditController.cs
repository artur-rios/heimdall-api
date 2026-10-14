using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Mediator.Query;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.Heimdall.WebApi.Controllers;

[Route("api/audit-log")]
public class AuditController(QueryMediator queryMediator) : Controller
{
    /// <summary>
    ///     Lists the audit trail (UC-48, FR-AT-01/02), newest first. System Admin only. Every filter is
    ///     optional and they combine: <c>from</c>/<c>to</c> bound when an entry was recorded (UTC,
    ///     inclusive), <c>actorId</c> and <c>targetId</c> are public ids, <c>action</c> is the exact
    ///     command type name (e.g. <c>CreateScopeCommand</c>), and <c>succeeded=false</c> lists only
    ///     refusals.
    /// </summary>
    /// <remarks>
    ///     Entries are shown as stored. An actor the retention pass has cleared (NFR-21) is
    ///     <c>null</c> and is never reconstructed, so filtering by <c>actorId</c> cannot find entries
    ///     whose attribution has been cleared. A Scope Admin is refused: the trail has no scope
    ///     column, and an old entry's target may be gone, so it cannot be divided by scope reliably.
    ///     Reading the trail is not itself audited.
    /// </remarks>
    [HttpGet]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<PaginatedOutput<AuditLogEntryOutput>>> List([FromQuery] ListAuditLogQuery query)
    {
        HttpContext.ApplyActor(query);

        var result = await queryMediator
            .ExecutePaginatedQueryAsync<ListAuditLogQuery, AuditLogEntryOutput>(query);

        return result.ToActionResult(statusMap: AuditMessageMap.StatusCodes);
    }
}
