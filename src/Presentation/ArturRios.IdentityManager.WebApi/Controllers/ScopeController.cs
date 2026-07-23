using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Messages;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.Mediator.Command;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.IdentityManager.WebApi.Controllers;

[Route("api/scopes")]
public class ScopeController(CommandMediator commandMediator) : Controller
{
    /// <summary>
    ///     Creates a new scope with one or more initial owners (UC-01). Restricted to System Admins.
    /// </summary>
    [HttpPost]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<DataOutput<CreateScopeCommandOutput?>>> Create([FromBody] CreateScopeCommand command)
    {
        var result = await commandMediator
            .ExecuteCommandAsync<CreateScopeCommand, CreateScopeCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: ScopeMessageMap.StatusCodes);
    }
}
