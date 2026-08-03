using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.WebApi.Security;
using ArturRios.Mediator.Query;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.IdentityManager.WebApi.Controllers;

[Route("api/scopes/{scopeId:guid}/google-users")]
public class GoogleUserController(QueryMediator queryMediator) : Controller
{
    /// <summary>
    ///     Lists the Google Users of a scope, with pagination and optional name/email filters (UC-27,
    ///     FR-GO-14). A System Admin or an owner of the scope may call it; the ownership check
    ///     (AF-27b) is enforced by the handler from the acting caller.
    /// </summary>
    /// <remarks>
    ///     Unlike <see cref="GetById" /> this does carry a <c>RoleRequirement</c>: the authorization
    ///     matrix grants a Google User a read of themselves, never a listing, so every <c>User</c> is
    ///     refused here before the handler runs.
    /// </remarks>
    [HttpGet]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<PaginatedOutput<GoogleUserOutput>>> List(
        Guid scopeId, [FromQuery] ListScopeGoogleUsersQuery query)
    {
        query.ScopeId = scopeId;
        HttpContext.ApplyActor(query);

        var result = await queryMediator
            .ExecutePaginatedQueryAsync<ListScopeGoogleUsersQuery, GoogleUserOutput>(query);

        return ResponseResolver.Resolve(result, statusMap: GoogleUserMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Retrieves a single Google User by its public identifier within a scope (UC-27, FR-GO-14).
    /// </summary>
    /// <remarks>
    ///     Open to any authenticated actor, deliberately. UC-27 names a Google User as one of its
    ///     three actors — they may read their own record — and a Google User's token carries the
    ///     <c>User</c> role (FR-GO-04), so any attribute strong enough to keep other Users out would
    ///     lock out the actor the use case grants. The per-actor rule (AF-27b) is data-dependent and
    ///     is therefore the handler's, as UC-07's by-id read is.
    /// </remarks>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DataOutput<GoogleUserOutput?>>> GetById(
        Guid scopeId, Guid id, [FromQuery] bool includeDeleted = false)
    {
        var query = new GetGoogleUserByIdQuery { ScopeId = scopeId, Id = id, IncludeDeleted = includeDeleted };
        HttpContext.ApplyActor(query);

        var result = await queryMediator.ExecuteQueryAsync<GetGoogleUserByIdQuery, GoogleUserOutput>(query);

        return ResponseResolver.Resolve(result, statusMap: GoogleUserMessageMap.StatusCodes);
    }
}
