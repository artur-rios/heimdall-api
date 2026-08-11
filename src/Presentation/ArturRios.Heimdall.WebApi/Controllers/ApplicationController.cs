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

[Route("api/scopes/{scopeId:guid}/applications")]
public class ApplicationController(CommandMediator commandMediator, QueryMediator queryMediator) : Controller
{
    /// <summary>
    ///     Registers an application within a scope (UC-16, FR-AP-01/02/03). Only a System Admin or a
    ///     Scope Admin may call it: FR-AP-03 restricts ownership to a <c>ScopeAdmin</c> who owns the
    ///     scope, so a <c>User</c> has nothing to create here and the attribute refuses them. The
    ///     remaining rules depend on data the attribute cannot see and are enforced by the handler —
    ///     the acting Scope Admin must own the scope (AF-16e) and may only name themself (AF-16c),
    ///     and the named owner must satisfy FR-AP-03 (AF-16b).
    /// </summary>
    [HttpPost]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<CreateApplicationCommandOutput?>>> Create(
        Guid scopeId, [FromBody] CreateApplicationCommand command)
    {
        command.ScopeId = scopeId;
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<CreateApplicationCommand, CreateApplicationCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: ApplicationMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Retrieves a single application by its public identifier within the scope (UC-17,
    ///     FR-AP-04/09). A <c>User</c> can own no application (FR-AP-03) and so is refused by the
    ///     attribute; among the remaining actors the rule is data-dependent and lives in the handler —
    ///     a System Admin sees any application, a Scope Admin only the ones they own (AF-17b).
    /// </summary>
    [HttpGet("{id:guid}")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<ApplicationOutput?>>> GetById(
        Guid scopeId, Guid id, [FromQuery] bool includeDeleted = false)
    {
        var query = new GetApplicationByIdQuery { ScopeId = scopeId, Id = id, IncludeDeleted = includeDeleted };
        HttpContext.ApplyActor(query);

        var result = await queryMediator.ExecuteQueryAsync<GetApplicationByIdQuery, ApplicationOutput>(query);

        return ResponseResolver.Resolve(result, statusMap: ApplicationMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Lists the applications of a scope (UC-17, FR-AP-05/09). A System Admin sees every
    ///     application in the scope; a Scope Admin must own the scope (AF-17b) and sees only the
    ///     applications they own. Both narrowings are enforced by the handler from the acting user.
    /// </summary>
    [HttpGet]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<PaginatedOutput<ApplicationOutput>>> List(
        Guid scopeId, [FromQuery] ListScopeApplicationsQuery query)
    {
        query.ScopeId = scopeId;
        HttpContext.ApplyActor(query);

        var result = await queryMediator
            .ExecutePaginatedQueryAsync<ListScopeApplicationsQuery, ApplicationOutput>(query);

        return ResponseResolver.Resolve(result, statusMap: ApplicationMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Updates an application's name and owner (UC-18, FR-AP-06). A <c>User</c> can own no
    ///     application (FR-AP-03) and so is refused by the attribute; among the remaining actors the
    ///     rule is data-dependent and lives in the handler — a System Admin updates any application, a
    ///     Scope Admin only the ones they own (AF-18c). The handler also resolves the application
    ///     inside the addressed scope (AF-18a) and, when the owner changes, checks the new owner
    ///     against FR-AP-03 (AF-18b).
    /// </summary>
    [HttpPut("{id:guid}")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<UpdateApplicationCommandOutput?>>> Update(
        Guid scopeId, Guid id, [FromBody] UpdateApplicationCommand command)
    {
        command.ScopeId = scopeId;
        command.Id = id;
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<UpdateApplicationCommand, UpdateApplicationCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: ApplicationMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Logically deletes an application by setting <c>IsDeleted = true</c> (UC-19, FR-AP-07). A
    ///     <c>User</c> can own no application (FR-AP-03) and so is refused by the attribute; among the
    ///     remaining actors the rule is data-dependent and lives in the handler — a System Admin
    ///     deletes any application, a Scope Admin only the ones they own (AF-19c). The handler also
    ///     resolves the application inside the addressed scope (AF-19a) and answers an already-deleted
    ///     application idempotently (AF-19b).
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<DeleteApplicationCommandOutput?>>> Delete(Guid scopeId, Guid id)
    {
        var command = new DeleteApplicationCommand { ScopeId = scopeId, Id = id };
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<DeleteApplicationCommand, DeleteApplicationCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: ApplicationMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Permanently (hard) deletes an application, removing the record from the database (UC-20,
    ///     FR-AP-08). Restricted to System Admins: a Scope Admin may logically delete an application
    ///     they own (UC-19), but never purge it, so the attribute settles authorization on its own and
    ///     the handler applies no further rule. The handler resolves the application inside the
    ///     addressed scope in any deletion state and reports AF-20a when it does not exist — which
    ///     includes a repeated call, as the removal leaves nothing to find. Nothing cascades: the
    ///     application's scope and owner are untouched.
    /// </summary>
    [HttpDelete("{id:guid}/hard")]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<DataOutput<HardDeleteApplicationCommandOutput?>>> HardDelete(
        Guid scopeId, Guid id)
    {
        var result = await commandMediator
            .ExecuteCommandAsync<HardDeleteApplicationCommand, HardDeleteApplicationCommandOutput>(
                new HardDeleteApplicationCommand { ScopeId = scopeId, Id = id });

        return ResponseResolver.Resolve(result, statusMap: ApplicationMessageMap.StatusCodes);
    }
}
