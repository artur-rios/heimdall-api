using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.WebApi.Security;
using ArturRios.Mediator.Command;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.IdentityManager.WebApi.Controllers;

[Route("api/scopes/{scopeId:guid}/applications")]
public class ApplicationController(CommandMediator commandMediator) : Controller
{
    /// <summary>
    ///     Registers an application within a scope (UC-16, FR-AP-01/02/03). Open to any authenticated
    ///     actor because all three roles may create one — a System Admin anywhere, a Scope Admin in a
    ///     scope they own, and a User with themself as owner — and each rule depends on data the
    ///     attribute cannot see. The handler therefore enforces them, along with the owner-eligibility
    ///     check (AF-16b) and the User self-owner rule (AF-16c).
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<DataOutput<CreateApplicationCommandOutput?>>> Create(
        Guid scopeId, [FromBody] CreateApplicationCommand command)
    {
        command.ScopeId = scopeId;
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<CreateApplicationCommand, CreateApplicationCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: ApplicationMessageMap.StatusCodes);
    }
}
