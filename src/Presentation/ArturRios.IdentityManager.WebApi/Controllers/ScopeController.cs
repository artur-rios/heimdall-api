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

[Route("api/scopes")]
public class ScopeController(CommandMediator commandMediator, QueryMediator queryMediator) : Controller
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

    /// <summary>
    ///     Updates an existing scope's name and description (UC-03). Restricted to System Admins.
    /// </summary>
    [HttpPut("{id:guid}")]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<DataOutput<UpdateScopeCommandOutput?>>> Update(
        Guid id, [FromBody] UpdateScopeCommand command)
    {
        command.Id = id;

        var result = await commandMediator
            .ExecuteCommandAsync<UpdateScopeCommand, UpdateScopeCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: ScopeMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Logically deletes a scope, cascading to its Users, Google Users, and applications (UC-04).
    ///     Restricted to System Admins.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<DataOutput<DeleteScopeCommandOutput?>>> Delete(Guid id)
    {
        var result = await commandMediator
            .ExecuteCommandAsync<DeleteScopeCommand, DeleteScopeCommandOutput>(new DeleteScopeCommand { Id = id });

        return ResponseResolver.Resolve(result, statusMap: ScopeMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Permanently (hard) deletes a scope, removing its Users, Google Users, applications, and
    ///     ownership/membership join rows (UC-05). Restricted to System Admins.
    /// </summary>
    [HttpDelete("{id:guid}/hard")]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<DataOutput<HardDeleteScopeCommandOutput?>>> HardDelete(Guid id)
    {
        var result = await commandMediator
            .ExecuteCommandAsync<HardDeleteScopeCommand, HardDeleteScopeCommandOutput>(
                new HardDeleteScopeCommand { Id = id });

        return ResponseResolver.Resolve(result, statusMap: ScopeMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Lists scopes with pagination and optional filtering (UC-02). Restricted to System Admins.
    /// </summary>
    [HttpGet]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<PaginatedOutput<ScopeOutput>>> List([FromQuery] ListScopesQuery query)
    {
        var result = await queryMediator
            .ExecutePaginatedQueryAsync<ListScopesQuery, ScopeOutput>(query);

        return ResponseResolver.Resolve(result, statusMap: ScopeMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Retrieves a single scope by its public identifier (UC-02). Open to any authenticated actor
    ///     because a Scope Admin reads the scopes they own and a User the scope they belong to; that
    ///     per-actor visibility rule (AF-02b) is data-dependent and is therefore enforced by the
    ///     handler.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DataOutput<ScopeOutput?>>> GetById(Guid id, [FromQuery] bool includeDeleted = false)
    {
        var query = new GetScopeByIdQuery { Id = id, IncludeDeleted = includeDeleted };
        HttpContext.ApplyActor(query);

        var result = await queryMediator.ExecuteQueryAsync<GetScopeByIdQuery, ScopeOutput>(query);

        return ResponseResolver.Resolve(result, statusMap: ScopeMessageMap.StatusCodes);
    }
}
