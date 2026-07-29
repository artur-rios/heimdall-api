using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Mediator.Command;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using ArturRios.Util.WebApi.Security.Attributes;
using ArturRios.Util.WebApi.Security.Records;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.IdentityManager.WebApi.Controllers;

[Route("api")]
public class PersonController(CommandMediator commandMediator) : Controller
{
    /// <summary>
    ///     Creates a <c>ScopeAdmin</c> or <c>SystemAdmin</c> person with no scope (UC-06 path b).
    ///     Restricted to System Admins (AF-06c).
    /// </summary>
    [HttpPost("persons")]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<DataOutput<CreatePersonCommandOutput?>>> CreateAdmin(
        [FromBody] CreateAdminCommand command)
    {
        var result = await commandMediator
            .ExecuteCommandAsync<CreateAdminCommand, CreatePersonCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Creates a <c>User</c> within a scope (UC-06 path a). A System Admin or an owner of the scope
    ///     may call it; the ownership check (AF-06e) is enforced by the handler from the acting user.
    /// </summary>
    [HttpPost("scopes/{scopeId:guid}/persons")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<CreatePersonCommandOutput?>>> CreateUser(
        Guid scopeId, [FromBody] CreateUserCommand command)
    {
        command.ScopeId = scopeId;

        var actor = (AuthenticatedUser)HttpContext.Items["User"]!;
        command.ActingPersonId = actor.Id;
        command.ActingRole = actor.Role;

        var result = await commandMediator
            .ExecuteCommandAsync<CreateUserCommand, CreatePersonCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Creates a brand-new <c>ScopeAdmin</c> person directly as a co-owner of a scope (UC-06 path
    ///     c, FR-SC-12). A System Admin or an owner of the scope may call it; the ownership check
    ///     (AF-06e) is enforced by the handler from the acting user.
    /// </summary>
    [HttpPost("scopes/{scopeId:guid}/owners")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<CreatePersonCommandOutput?>>> CreateScopeOwner(
        Guid scopeId, [FromBody] CreateScopeOwnerCommand command)
    {
        command.ScopeId = scopeId;

        var actor = (AuthenticatedUser)HttpContext.Items["User"]!;
        command.ActingPersonId = actor.Id;
        command.ActingRole = actor.Role;

        var result = await commandMediator
            .ExecuteCommandAsync<CreateScopeOwnerCommand, CreatePersonCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }
}
