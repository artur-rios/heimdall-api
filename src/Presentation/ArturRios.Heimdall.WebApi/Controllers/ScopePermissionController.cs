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

[Route("api/scopes/{scopeId:guid}/permissions")]
public class ScopePermissionController(CommandMediator commandMediator, QueryMediator queryMediator) : Controller
{
    /// <summary>
    ///     Creates a scope-specific permission within a scope (UC-31, FR-SP-01/02). Only a System Admin
    ///     or a Scope Admin may call it: the endpoint's <c>[RoleRequirement]</c> refuses a <c>User</c>,
    ///     who has no standing to manage a scope's permissions. The remaining rules depend on data the
    ///     attribute cannot see and are enforced by the handler — the target scope must exist and be
    ///     active (AF-31a), the input must be well-formed (AF-31d), and an acting Scope Admin must own
    ///     the scope (AF-31e). A scope permission has no owner of its own, so owning the scope is the
    ///     whole of the authorization.
    /// </summary>
    [HttpPost]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<CreateScopePermissionCommandOutput?>>> Create(
        Guid scopeId, [FromBody] CreateScopePermissionCommand command)
    {
        command.ScopeId = scopeId;
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<CreateScopePermissionCommand, CreateScopePermissionCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: ScopePermissionMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Retrieves a single scope permission by its public identifier within the scope (UC-32,
    ///     FR-SP-04/09). A <c>User</c> is refused by the attribute; among the remaining actors the
    ///     rule is data-dependent and lives in the handler — a System Admin sees any permission, a
    ///     Scope Admin only one whose scope they own (AF-32e). A permission that does not exist under
    ///     the addressed scope, or that is logically deleted and was not explicitly requested, is
    ///     AF-32a.
    /// </summary>
    [HttpGet("{id:guid}")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<ScopePermissionOutput?>>> GetById(
        Guid scopeId, Guid id, [FromQuery] bool includeDeleted = false)
    {
        var query = new GetScopePermissionByIdQuery { ScopeId = scopeId, Id = id, IncludeDeleted = includeDeleted };
        HttpContext.ApplyActor(query);

        var result = await queryMediator.ExecuteQueryAsync<GetScopePermissionByIdQuery, ScopePermissionOutput>(query);

        return ResponseResolver.Resolve(result, statusMap: ScopePermissionMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Lists the permissions of a scope (UC-32, FR-SP-05/09). A System Admin sees every
    ///     permission in the scope; a Scope Admin must own the scope (AF-32e) and then sees every
    ///     permission in it — a scope permission has no owner of its own, so there is no per-owner
    ///     narrowing. Both the ownership gate and the scope lookup are enforced by the handler from
    ///     the acting user; a missing or logically deleted scope reuses AF-31a.
    /// </summary>
    [HttpGet]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<PaginatedOutput<ScopePermissionOutput>>> List(
        Guid scopeId, [FromQuery] ListScopePermissionsQuery query)
    {
        query.ScopeId = scopeId;
        HttpContext.ApplyActor(query);

        var result = await queryMediator
            .ExecutePaginatedQueryAsync<ListScopePermissionsQuery, ScopePermissionOutput>(query);

        return ResponseResolver.Resolve(result, statusMap: ScopePermissionMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Updates a scope permission's name, description, and JWT-claim flag (UC-33, FR-SP-06). A
    ///     <c>User</c> is refused by the attribute; among the remaining actors the rule is
    ///     data-dependent and lives in the handler — a System Admin updates any permission, a Scope
    ///     Admin only one whose scope they own (AF-33e). The handler resolves the permission inside
    ///     the addressed scope (AF-33a) and validates the input shape, reusing UC-31's messages.
    /// </summary>
    [HttpPut("{id:guid}")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<UpdateScopePermissionCommandOutput?>>> Update(
        Guid scopeId, Guid id, [FromBody] UpdateScopePermissionCommand command)
    {
        command.ScopeId = scopeId;
        command.Id = id;
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<UpdateScopePermissionCommand, UpdateScopePermissionCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: ScopePermissionMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Logically deletes a scope permission by setting <c>IsDeleted = true</c> (UC-34, FR-SP-07).
    ///     A <c>User</c> is refused by the attribute; among the remaining actors the rule is
    ///     data-dependent and lives in the handler — a System Admin deletes any permission, a Scope
    ///     Admin only one whose scope they own (AF-34e). The handler resolves the permission inside
    ///     the addressed scope (AF-34a) and answers an already-deleted permission idempotently
    ///     (AF-34b). Nothing cascades: a scope permission owns no dependent row.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<DeleteScopePermissionCommandOutput?>>> Delete(Guid scopeId, Guid id)
    {
        var command = new DeleteScopePermissionCommand { ScopeId = scopeId, Id = id };
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<DeleteScopePermissionCommand, DeleteScopePermissionCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: ScopePermissionMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Permanently (hard) deletes a scope permission, removing the record from the database
    ///     (UC-35, FR-SP-08). Restricted to System Admins: a Scope Admin may logically delete a
    ///     permission in a scope they own (UC-34), but never purge it, so the attribute settles
    ///     authorization on its own and the handler applies no further rule. The handler resolves the
    ///     permission inside the addressed scope in any deletion state and reports AF-35a when it
    ///     does not exist — which includes a repeated call, as the removal leaves nothing to find.
    ///     Nothing cascades: the permission's scope is untouched.
    /// </summary>
    [HttpDelete("{id:guid}/hard")]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<DataOutput<HardDeleteScopePermissionCommandOutput?>>> HardDelete(
        Guid scopeId, Guid id)
    {
        var result = await commandMediator
            .ExecuteCommandAsync<HardDeleteScopePermissionCommand, HardDeleteScopePermissionCommandOutput>(
                new HardDeleteScopePermissionCommand { ScopeId = scopeId, Id = id });

        return ResponseResolver.Resolve(result, statusMap: ScopePermissionMessageMap.StatusCodes);
    }
}
