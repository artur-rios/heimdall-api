using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.WebApi.Security;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Query;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.IdentityManager.WebApi.Controllers;

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
}
