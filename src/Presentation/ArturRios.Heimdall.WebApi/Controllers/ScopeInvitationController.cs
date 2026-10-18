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
}
