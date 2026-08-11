using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Query;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.Heimdall.WebApi.Controllers;

[Route("api/scopes/{scopeId:guid}/google-users")]
public class GoogleUserController(CommandMediator commandMediator, QueryMediator queryMediator) : Controller
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

    /// <summary>
    ///     Logically deletes a Google User by setting <c>IsDeleted = true</c> (UC-28, FR-GO-15). The
    ///     attribute keeps every <c>User</c> out — the authorization matrix withholds this from them,
    ///     Google or password alike — while the rule that depends on data it cannot see is the
    ///     handler's: whether the acting Scope Admin owns the Google User's scope (AF-28c).
    /// </summary>
    /// <remarks>
    ///     Unlike <see cref="GetById" /> there is no actor a role attribute would wrongly exclude
    ///     here, which is why this one carries the attribute the by-id read cannot.
    /// </remarks>
    [HttpDelete("{id:guid}")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<DeleteGoogleUserCommandOutput?>>> Delete(Guid scopeId, Guid id)
    {
        var command = new DeleteGoogleUserCommand { ScopeId = scopeId, Id = id };
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<DeleteGoogleUserCommand, DeleteGoogleUserCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: GoogleUserMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Permanently (hard) deletes a Google User, removing the record for good (UC-29, FR-GO-16).
    ///     Restricted to System Admins — the authorization matrix withholds this even from an owning
    ///     Scope Admin, who may only delete logically (UC-28).
    /// </summary>
    /// <remarks>
    ///     The one Google User endpoint where the attribute is the whole authorization rule: UC-29
    ///     names a single actor and nothing about the decision depends on data, so the handler applies
    ///     none and the command carries no acting person. Nothing cascades either — a Google User owns
    ///     no dependent row.
    /// </remarks>
    [HttpDelete("{id:guid}/hard")]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<DataOutput<HardDeleteGoogleUserCommandOutput?>>> HardDelete(
        Guid scopeId, Guid id)
    {
        var result = await commandMediator
            .ExecuteCommandAsync<HardDeleteGoogleUserCommand, HardDeleteGoogleUserCommandOutput>(
                new HardDeleteGoogleUserCommand { ScopeId = scopeId, Id = id });

        return ResponseResolver.Resolve(result, statusMap: GoogleUserMessageMap.StatusCodes);
    }
}
