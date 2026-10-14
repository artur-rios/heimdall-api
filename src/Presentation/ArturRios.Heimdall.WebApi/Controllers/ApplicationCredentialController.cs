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
public class ApplicationCredentialController(CommandMediator commandMediator, QueryMediator queryMediator) : Controller
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
        // AF-53c — a body that is not JSON, or an expiresAt that is not a date-time, never reaches here:
        // MalformedRequestFilter answers it (NFR-10). Only a genuinely empty body means "no expiry".
        command ??= new IssueApplicationCredentialCommand();
        command.ScopeId = scopeId;
        command.Id = id;
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<IssueApplicationCredentialCommand, IssueApplicationCredentialCommandOutput>(command);

        return result.ToActionResult(statusMap: ApplicationCredentialMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Lists the application's credentials, newest first, with whether each is live (UC-54).
    ///     Never a secret or a hash.
    /// </summary>
    [HttpGet]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<ApplicationCredentialListOutput?>>> List(Guid scopeId, Guid id)
    {
        var query = new ListApplicationCredentialsQuery { ScopeId = scopeId, Id = id };
        HttpContext.ApplyActor(query);

        var result = await queryMediator
            .ExecuteQueryAsync<ListApplicationCredentialsQuery, ApplicationCredentialListOutput>(query);

        return result.ToActionResult(statusMap: ApplicationCredentialMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Revokes one credential (UC-55). Its secret stops being exchanged at once, and every token
    ///     already issued from it fails the next live re-check (UC-57).
    /// </summary>
    [HttpDelete("{credentialId:guid}")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<RevokeApplicationCredentialCommandOutput?>>> Revoke(
        Guid scopeId, Guid id, Guid credentialId)
    {
        var command = new RevokeApplicationCredentialCommand { ScopeId = scopeId, Id = id, CredentialId = credentialId };
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<RevokeApplicationCredentialCommand, RevokeApplicationCredentialCommandOutput>(command);

        return result.ToActionResult(statusMap: ApplicationCredentialMessageMap.StatusCodes);
    }
}
