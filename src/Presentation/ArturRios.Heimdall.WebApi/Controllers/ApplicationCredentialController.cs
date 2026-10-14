using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Mediator.Command;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ArturRios.Heimdall.WebApi.Controllers;

/// <summary>
///     An application's credentials (UC-53…UC-55, FR-AP-10…FR-AP-12): the secrets it exchanges for an
///     application token at <c>POST /api/applications/token</c>.
/// </summary>
/// <remarks>
///     A <c>User</c> can own no application (FR-AP-03) and is refused by the attribute. Among System
///     Admins and Scope Admins the rule is data-dependent and lives in the handlers, through
///     <c>ApplicationManagementAccess</c> — the same rule as reading or renaming the application.
/// </remarks>
[Route("api/scopes/{scopeId:guid}/applications/{id:guid}/credentials")]
public class ApplicationCredentialController(CommandMediator commandMediator) : Controller
{
    /// <summary>
    ///     Issues a credential (UC-53). The body is optional and carries only <c>expiresAt</c>. The
    ///     answer is the only place the secret ever appears; Heimdall keeps its hash alone.
    /// </summary>
    [HttpPost]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<IssueApplicationCredentialCommandOutput?>>> Issue(
        Guid scopeId, Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] IssueApplicationCredentialCommand? command)
    {
        command ??= new IssueApplicationCredentialCommand();
        command.ScopeId = scopeId;
        command.Id = id;
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<IssueApplicationCredentialCommand, IssueApplicationCredentialCommandOutput>(command);

        return result.ToActionResult(statusMap: ApplicationCredentialMessageMap.StatusCodes);
    }
}
