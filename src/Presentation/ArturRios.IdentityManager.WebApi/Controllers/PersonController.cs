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

[Route("api")]
public class PersonController(CommandMediator commandMediator, QueryMediator queryMediator) : Controller
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
        HttpContext.ApplyActor(command);

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
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<CreateScopeOwnerCommand, CreatePersonCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Adds an existing <c>ScopeAdmin</c> person as an additional owner of a scope (UC-21,
    ///     FR-SC-08/FR-SC-09). The attribute keeps a <c>User</c> out — they can never be a System
    ///     Admin nor an existing owner — while the rules that depend on data it cannot see are the
    ///     handler's: whether the acting Scope Admin owns this scope (AF-21c), whether the scope is
    ///     active (AF-21a), whether the named person is a usable <c>ScopeAdmin</c> (AF-21b), and
    ///     whether they already own it (AF-21d).
    /// </summary>
    [HttpPost("scopes/{scopeId:guid}/owners/{personId:guid}")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<AddScopeOwnerCommandOutput?>>> AddScopeOwner(
        Guid scopeId, Guid personId)
    {
        var command = new AddScopeOwnerCommand { ScopeId = scopeId, PersonId = personId };
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<AddScopeOwnerCommand, AddScopeOwnerCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Updates a person's name and email, and — for a System Admin — their role (UC-08). Open to
    ///     any authenticated actor because a User may update their own record; the per-actor rule and
    ///     the role-change restriction (AF-08c) are enforced by the handler.
    /// </summary>
    [HttpPut("persons/{id:guid}")]
    public async Task<ActionResult<DataOutput<UpdatePersonCommandOutput?>>> Update(
        Guid id, [FromBody] UpdatePersonCommand command)
    {
        command.Id = id;
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<UpdatePersonCommand, UpdatePersonCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Logically deletes a person by setting <c>IsDeleted = true</c> (UC-09, FR-PE-06). The
    ///     attribute keeps a plain <c>User</c> out; the owner rule (AF-09c) is data-dependent and is
    ///     therefore enforced by the handler, as are the self-deletion (AF-09d) and last-owner
    ///     (AF-09e) refusals.
    /// </summary>
    [HttpDelete("persons/{id:guid}")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<DeletePersonCommandOutput?>>> Delete(Guid id)
    {
        var command = new DeletePersonCommand { Id = id };
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<DeletePersonCommand, DeletePersonCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Permanently (hard) deletes a person, removing the applications they own, their password
    ///     reset and email verification tokens, and their scope membership/ownership rows (UC-10,
    ///     FR-PE-07). Restricted to System Admins; the self-deletion refusal (AF-10c) and the
    ///     last-owner guard (AF-10b) are enforced by the handler.
    /// </summary>
    [HttpDelete("persons/{id:guid}/hard")]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<DataOutput<HardDeletePersonCommandOutput?>>> HardDelete(Guid id)
    {
        var command = new HardDeletePersonCommand { Id = id };
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<HardDeletePersonCommand, HardDeletePersonCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Retrieves a single person by their public identifier (UC-07, FR-PE-03). Open to any
    ///     authenticated actor; the per-actor visibility rule (AF-07b) is data-dependent and is
    ///     therefore enforced by the handler.
    /// </summary>
    [HttpGet("persons/{id:guid}")]
    public async Task<ActionResult<DataOutput<PersonOutput?>>> GetById(
        Guid id, [FromQuery] bool includeDeleted = false)
    {
        var query = new GetPersonByIdQuery { Id = id, IncludeDeleted = includeDeleted };
        HttpContext.ApplyActor(query);

        var result = await queryMediator.ExecuteQueryAsync<GetPersonByIdQuery, PersonOutput>(query);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Lists the <c>User</c> persons of a scope (UC-07, FR-PE-04). A System Admin or an owner of
    ///     the scope may call it; the ownership check (AF-07b) is enforced by the handler from the
    ///     acting user.
    /// </summary>
    [HttpGet("scopes/{scopeId:guid}/persons")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<PaginatedOutput<PersonOutput>>> ListScopePersons(
        Guid scopeId, [FromQuery] ListScopePersonsQuery query)
    {
        query.ScopeId = scopeId;
        HttpContext.ApplyActor(query);

        var result = await queryMediator
            .ExecutePaginatedQueryAsync<ListScopePersonsQuery, PersonOutput>(query);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Lists the <c>ScopeAdmin</c> owners of a scope (UC-07, FR-PE-04). A System Admin or an
    ///     owner of the scope may call it; the ownership check (AF-07b) is enforced by the handler
    ///     from the acting user.
    /// </summary>
    [HttpGet("scopes/{scopeId:guid}/owners")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<PaginatedOutput<PersonOutput>>> ListScopeOwners(
        Guid scopeId, [FromQuery] ListScopeOwnersQuery query)
    {
        query.ScopeId = scopeId;
        HttpContext.ApplyActor(query);

        var result = await queryMediator
            .ExecutePaginatedQueryAsync<ListScopeOwnersQuery, PersonOutput>(query);

        return ResponseResolver.Resolve(result, statusMap: PersonMessageMap.StatusCodes);
    }
}
