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

/// <summary>
///     Scope owner invitations (FR-SC-14…FR-SC-18): what a scope's owners see and withdraw (UC-58), and what
///     an invited administrator lists, accepts and declines (UC-59…UC-61). An invitation is opened by
///     <c>POST /api/scopes/{scopeId}/owners/{personId}</c> (UC-21) when a Scope Admin asks.
/// </summary>
[Route("api")]
public class ScopeInvitationController(CommandMediator commandMediator, QueryMediator queryMediator) : Controller
{
    /// <summary>
    ///     Lists the scope's open owner invitations (UC-58, FR-SC-15), newest first: each one's id, the invitee's
    ///     id, name and address, the inviter's name, and when it was made and expires. A System Admin or an
    ///     owner of the scope may call it; the attribute keeps a <c>User</c> out, and the handler checks the
    ///     scope (AF-58a) and the ownership (AF-58b).
    /// </summary>
    [HttpGet("scopes/{scopeId:guid}/owner-invitations")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<PaginatedOutput<ScopeOwnerInvitationOutput>>> ListForScope(
        Guid scopeId, [FromQuery] ListScopeOwnerInvitationsQuery query)
    {
        query.ScopeId = scopeId;
        HttpContext.ApplyActor(query);

        var result = await queryMediator
            .ExecutePaginatedQueryAsync<ListScopeOwnerInvitationsQuery, ScopeOwnerInvitationOutput>(query);

        return result.ToActionResult(statusMap: ScopeInvitationMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Withdraws an open owner invitation of the scope (UC-58, FR-SC-15): 204 with no body. An ended,
    ///     expired or unknown invitation, or another scope's, answers 404 "The invitation was not found or is
    ///     no longer open." (AF-58c). Same actors as the listing.
    /// </summary>
    [HttpDelete("scopes/{scopeId:guid}/owner-invitations/{invitationId:guid}")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Withdraw(Guid scopeId, Guid invitationId)
    {
        var command = new WithdrawScopeOwnerInvitationCommand { ScopeId = scopeId, InvitationId = invitationId };
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<WithdrawScopeOwnerInvitationCommand, WithdrawScopeOwnerInvitationCommandOutput>(command);

        return result.Success
            ? NoContent()
            : result.ToActionResult(statusMap: ScopeInvitationMessageMap.StatusCodes).Result!;
    }

    /// <summary>
    ///     Lists the caller's own open scope owner invitations (UC-59, FR-SC-16), soonest to expire first: each
    ///     one's id, the scope's id and name, the inviter's name, and when it was made and expires. Any signed-in
    ///     Scope Admin or System Admin may call it; a <c>User</c> cannot be invited, so the attribute keeps them
    ///     out.
    /// </summary>
    [HttpGet("persons/me/scope-invitations")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<PaginatedOutput<MyScopeInvitationOutput>>> ListMine(
        [FromQuery] ListMyScopeInvitationsQuery query)
    {
        HttpContext.ApplyActor(query);

        var result = await queryMediator
            .ExecutePaginatedQueryAsync<ListMyScopeInvitationsQuery, MyScopeInvitationOutput>(query);

        return result.ToActionResult(statusMap: ScopeInvitationMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Accepts a scope owner invitation (UC-60, FR-SC-17): the invitation ends as accepted and the caller
    ///     becomes an owner of the scope, together. Only the invitee may; everybody else, and an ended or expired
    ///     invitation, gets 404 "The invitation was not found or is no longer open." (AF-60a). An invitation whose
    ///     scope was deleted, or whose invitee is no longer an unrestricted administrator, is voided and answered
    ///     the same (AF-60b).
    /// </summary>
    [HttpPost("scope-invitations/{invitationId:guid}/accept")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<AcceptScopeOwnerInvitationCommandOutput?>>> Accept(Guid invitationId)
    {
        var command = new AcceptScopeOwnerInvitationCommand { InvitationId = invitationId };
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<AcceptScopeOwnerInvitationCommand, AcceptScopeOwnerInvitationCommandOutput>(command);

        return result.ToActionResult(statusMap: ScopeInvitationMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Declines a scope owner invitation (UC-61, FR-SC-17). Same caller and refusals as UC-60 (AF-61a).
    /// </summary>
    [HttpPost("scope-invitations/{invitationId:guid}/decline")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<DeclineScopeOwnerInvitationCommandOutput?>>> Decline(Guid invitationId)
    {
        var command = new DeclineScopeOwnerInvitationCommand { InvitationId = invitationId };
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<DeclineScopeOwnerInvitationCommand, DeclineScopeOwnerInvitationCommandOutput>(command);

        return result.ToActionResult(statusMap: ScopeInvitationMessageMap.StatusCodes);
    }
}
