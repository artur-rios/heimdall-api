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
using Microsoft.AspNetCore.RateLimiting;

namespace ArturRios.Heimdall.WebApi.Controllers;

[Route("api")]
public class PersonController(CommandMediator commandMediator, QueryMediator queryMediator) : Controller
{
    /// <summary>
    ///     Creates a <c>ScopeAdmin</c> or <c>SystemAdmin</c> person with no scope (UC-06 path b).
    ///     Restricted to System Admins (AF-06c).
    /// </summary>
    /// <remarks>
    ///     Derives an Argon2id hash, so it is limited to thirty requests a minute per acting person and
    ///     thirty per address; a rejection answers 429 "Too many requests." with a <c>Retry-After</c>
    ///     header.
    /// </remarks>
    [HttpPost("persons")]
    [EnableRateLimiting(Startup.AccountCreationRateLimitPolicy)]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<DataOutput<CreatePersonCommandOutput?>>> CreateAdmin(
        [FromBody] CreateAdminCommand command)
    {
        var result = await commandMediator
            .ExecuteCommandAsync<CreateAdminCommand, CreatePersonCommandOutput>(command);

        return result.ToActionResult(statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Creates a <c>User</c> within a scope (UC-06 path a). A System Admin or an owner of the scope
    ///     may call it; the ownership check (AF-06e) is enforced by the handler from the acting user.
    /// </summary>
    /// <remarks>
    ///     Derives an Argon2id hash, so it is limited to thirty requests a minute per acting person and
    ///     thirty per address; a rejection answers 429 "Too many requests." with a <c>Retry-After</c>
    ///     header.
    /// </remarks>
    [HttpPost("scopes/{scopeId:guid}/persons")]
    [EnableRateLimiting(Startup.AccountCreationRateLimitPolicy)]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<CreatePersonCommandOutput?>>> CreateUser(
        Guid scopeId, [FromBody] CreateUserCommand command)
    {
        command.ScopeId = scopeId;
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<CreateUserCommand, CreatePersonCommandOutput>(command);

        return result.ToActionResult(statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Creates a brand-new <c>ScopeAdmin</c> person directly as a co-owner of a scope (UC-06 path
    ///     c, FR-SC-12). A System Admin or an owner of the scope may call it; the ownership check
    ///     (AF-06e) is enforced by the handler from the acting user.
    /// </summary>
    /// <remarks>
    ///     Derives an Argon2id hash, so it is limited to thirty requests a minute per acting person and
    ///     thirty per address; a rejection answers 429 "Too many requests." with a <c>Retry-After</c>
    ///     header.
    /// </remarks>
    [HttpPost("scopes/{scopeId:guid}/owners")]
    [EnableRateLimiting(Startup.AccountCreationRateLimitPolicy)]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<CreatePersonCommandOutput?>>> CreateScopeOwner(
        Guid scopeId, [FromBody] CreateScopeOwnerCommand command)
    {
        command.ScopeId = scopeId;
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<CreateScopeOwnerCommand, CreatePersonCommandOutput>(command);

        return result.ToActionResult(statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Adds an existing <c>ScopeAdmin</c> or <c>SystemAdmin</c> person as an additional owner of a scope
    ///     (UC-21, FR-SC-08/FR-SC-09). A System Admin adds them at once (201, or 200 when they already own
    ///     it). A Scope Admin owning the scope invites them instead (202 "The invitation was sent.", FR-SC-14):
    ///     they own nothing until they accept (UC-60), and a repeat while it is open answers the same and
    ///     mails nothing. The attribute keeps a <c>User</c> out; the handler checks the scope (AF-21a), the
    ///     ownership (AF-21c), the person (AF-21b) and an existing ownership (AF-21d).
    /// </summary>
    /// <remarks>
    ///     It can mail a third party, so it shares the account-creation limit: thirty requests a minute per
    ///     acting person and thirty per address; a rejection answers 429 "Too many requests." with a
    ///     <c>Retry-After</c> header.
    /// </remarks>
    [HttpPost("scopes/{scopeId:guid}/owners/{personId:guid}")]
    [EnableRateLimiting(Startup.AccountCreationRateLimitPolicy)]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    [ProducesResponseType<DataOutput<AddScopeOwnerCommandOutput>>(StatusCodes.Status200OK)]
    [ProducesResponseType<DataOutput<AddScopeOwnerCommandOutput>>(StatusCodes.Status201Created)]
    [ProducesResponseType<DataOutput<AddScopeOwnerCommandOutput>>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<DataOutput<AddScopeOwnerCommandOutput?>>> AddScopeOwner(
        Guid scopeId, Guid personId)
    {
        var command = new AddScopeOwnerCommand { ScopeId = scopeId, PersonId = personId };
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<AddScopeOwnerCommand, AddScopeOwnerCommandOutput>(command);

        return result.ToActionResult(statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Promotes an existing <c>User</c> of a scope to <c>ScopeAdmin</c>, making them a co-owner of
    ///     that scope (UC-23, FR-SC-08/FR-SC-13/FR-RO-03). The attribute keeps a <c>User</c> out —
    ///     they can never be a System Admin nor an existing owner — while the rules that depend on
    ///     data it cannot see are the handler's: whether the acting Scope Admin owns this scope
    ///     (AF-23c), whether the scope is active (AF-23a), whether the named person is a <c>User</c>
    ///     of it (AF-23b), and whether they already hold the role (AF-23d).
    /// </summary>
    /// <remarks>
    ///     The role is written only while the person is still the <c>User</c> the request read
    ///     (#237): when another request changed, promoted or deleted them in the meantime, nothing is
    ///     written and the answer is 409 "Concurrency conflict: the record was modified or removed by
    ///     another process." (AF-23f), or 409 "Conflict: a record with the same unique value already
    ///     exists." when an administrator took the address meanwhile. Read the person again before
    ///     retrying.
    /// </remarks>
    [HttpPost("scopes/{scopeId:guid}/users/{personId:guid}/promote")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    [ProducesResponseType<DataOutput<PromoteScopeUserCommandOutput>>(StatusCodes.Status200OK)]
    [ProducesResponseType<DataOutput<PromoteScopeUserCommandOutput>>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DataOutput<PromoteScopeUserCommandOutput?>>> PromoteScopeUser(
        Guid scopeId, Guid personId)
    {
        var command = new PromoteScopeUserCommand { ScopeId = scopeId, PersonId = personId };
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<PromoteScopeUserCommand, PromoteScopeUserCommandOutput>(command);

        return result.ToActionResult(statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Updates a person's name and email, and — for a System Admin — their role (UC-08). Open to
    ///     any authenticated actor because a User may update their own record; the per-actor rule and
    ///     the role-change restriction (AF-08c) are enforced by the handler. The response's
    ///     <c>ownedScopeIds</c> follows FR-PE-19, as <c>GET /api/persons/{id}</c> does.
    /// </summary>
    /// <remarks>
    ///     A caller changing their own email presents <c>currentPassword</c>, and <c>twoFactorCode</c>
    ///     when their second factor is active (FR-PE-14) — six digits for an authenticator-app code,
    ///     or a recovery code, matched as <c>2fa/verify</c> matches one (<c>XXXX-XXXX-XXXX-XXXX</c>,
    ///     hyphens, spaces and case ignored, the shorter <c>XXXX-XXXX</c> codes still accepted,
    ///     FR-2F-20) — so this action verifies a password like the
    ///     credential endpoints do, and sits under their rate limit (NFR-18), as every
    ///     password-verifying endpoint does. An own email change mails the previous address, so it is
    ///     charged to the person's account-email budget once the credentials are accepted: 429 "An
    ///     email of this kind was sent to you recently. Try again in a few minutes." inside the
    ///     cooldown, 429 "You have reached the limit for account emails. Try again later." once
    ///     the window's five are spent (AF-08j), both with <c>Retry-After</c>; nothing changes then, and
    ///     no recovery or email code is spent (an accepted authenticator code's time step is recorded,
    ///     as for any accepted code). While the caller's email sign-in method is enabled, an own
    ///     email change answers 409 "Turn off the email sign-in method before changing your email
    ///     address." (AF-08k), once the credentials are accepted. An address or role change is
    ///     written only while the person still holds the address and role the request read (#237):
    ///     when another request changed or deleted them in the meantime, neither the change nor the
    ///     name is written and the answer is 409 "Concurrency conflict: the record was modified or removed by
    ///     another process." (AF-08l), or 409 "Conflict: a record with the same unique value already
    ///     exists." when the new address was taken meanwhile. As for a failed save, an email change's
    ///     earlier steps stand: the person's tokens are already revoked and their reset and
    ///     verification links retired, and for an own change the account-email budget is charged and
    ///     any recovery or email code presented is spent. Read the person again before retrying.
    /// </remarks>
    [HttpPut("persons/{id:guid}")]
    [EnableRateLimiting(Startup.AuthEndpointRateLimitPolicy)]
    [ProducesResponseType<DataOutput<UpdatePersonCommandOutput>>(StatusCodes.Status200OK)]
    [ProducesResponseType<DataOutput<UpdatePersonCommandOutput>>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DataOutput<UpdatePersonCommandOutput?>>> Update(
        Guid id, [FromBody] UpdatePersonCommand command)
    {
        command.Id = id;
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<UpdatePersonCommand, UpdatePersonCommandOutput>(command);

        return result.ToActionResult(statusMap: PersonMessageMap.StatusCodes);
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

        return result.ToActionResult(statusMap: PersonMessageMap.StatusCodes);
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

        return result.ToActionResult(statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Removes a person's ownership of a scope (UC-22, FR-SC-08/FR-SC-10). The attribute keeps a
    ///     <c>User</c> out — they can never be a System Admin nor an existing owner — while the rules
    ///     that depend on data it cannot see are the handler's: whether the acting Scope Admin owns
    ///     this scope (AF-22c), whether the scope is active and the named person actually owns it
    ///     (AF-22a), and whether the scope would be left without an owner (AF-22b, NFR-12).
    /// </summary>
    [HttpDelete("scopes/{scopeId:guid}/owners/{personId:guid}")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<RemoveScopeOwnerCommandOutput?>>> RemoveScopeOwner(
        Guid scopeId, Guid personId)
    {
        var command = new RemoveScopeOwnerCommand { ScopeId = scopeId, PersonId = personId };
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<RemoveScopeOwnerCommand, RemoveScopeOwnerCommandOutput>(command);

        return result.ToActionResult(statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Retrieves a single person by their public identifier (UC-07, FR-PE-03). Open to any
    ///     authenticated actor; the per-actor visibility rule (AF-07b) is data-dependent and is
    ///     therefore enforced by the handler. <c>ownedScopeIds</c> lists every scope the person owns
    ///     for a System Admin and for the person themselves; a Scope Admin reading somebody else sees
    ///     only the scopes they own too (FR-PE-19).
    /// </summary>
    [HttpGet("persons/{id:guid}")]
    public async Task<ActionResult<DataOutput<PersonOutput?>>> GetById(
        Guid id, [FromQuery] bool includeDeleted = false)
    {
        var query = new GetPersonByIdQuery { Id = id, IncludeDeleted = includeDeleted };
        HttpContext.ApplyActor(query);

        var result = await queryMediator.ExecuteQueryAsync<GetPersonByIdQuery, PersonOutput>(query);

        return result.ToActionResult(statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Lists the <c>User</c> persons of a scope (UC-07, FR-PE-04). A System Admin or an owner of
    ///     the scope may call it; the ownership check (AF-07b) is enforced by the handler from the
    ///     acting user. <c>ownedScopeIds</c> is trimmed for a Scope Admin as on
    ///     <c>GET /api/persons/{id}</c> (FR-PE-19).
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

        return result.ToActionResult(statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Lists the owners of a scope (UC-07, FR-PE-04). A System Admin or an
    ///     owner of the scope may call it; the ownership check (AF-07b) is enforced by the handler
    ///     from the acting user. For a Scope Admin each owner's <c>ownedScopeIds</c> lists only the
    ///     scopes the caller owns too, this one among them; a System Admin sees them all (FR-PE-19).
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

        return result.ToActionResult(statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Lists the administrators who may own a scope (UC-07 read d, FR-PE-12) — every
    ///     <c>ScopeAdmin</c>, and for a System Admin caller every <c>SystemAdmin</c> too — projected to
    ///     identifier, name, and email: the source for an owner picker. A System Admin or a Scope
    ///     Admin may call it. Optionally excludes the current owners of a named scope, in which case
    ///     the handler requires the caller to be entitled to manage that scope.
    /// </summary>
    [HttpGet("persons/scope-admins")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<PaginatedOutput<PersonSummaryOutput>>> ListScopeAdmins(
        [FromQuery] ListScopeAdminsQuery query)
    {
        HttpContext.ApplyActor(query);

        var result = await queryMediator
            .ExecutePaginatedQueryAsync<ListScopeAdminsQuery, PersonSummaryOutput>(query);

        return result.ToActionResult(statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Lists the system's <c>SystemAdmin</c> persons (UC-07 read e, FR-PE-13), projected to
    ///     identifier, name, and email: who else holds full access to the deployment. System Admin
    ///     only.
    /// </summary>
    /// <remarks>
    ///     A Scope Admin is deliberately excluded: a System Admin's address is not tenant-facing,
    ///     which is also why <c>GET /api/persons/scope-admins</c> offers System Admins only to a
    ///     System Admin caller.
    /// </remarks>
    [HttpGet("persons/system-admins")]
    [RoleRequirement((int)Roles.SystemAdmin)]
    public async Task<ActionResult<PaginatedOutput<PersonSummaryOutput>>> ListSystemAdmins(
        [FromQuery] ListSystemAdminsQuery query)
    {
        HttpContext.ApplyActor(query);

        var result = await queryMediator
            .ExecutePaginatedQueryAsync<ListSystemAdminsQuery, PersonSummaryOutput>(query);

        return result.ToActionResult(statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Has a fresh verification link mailed to a person's own address (UC-51, FR-EV-06): outstanding
    ///     links are retired first, as UC-15 does, and the link uses the scope's verification URL. A
    ///     System Admin may ask for any person; a Scope Admin only for a <c>User</c> of a scope they own
    ///     (AF-51b). No body; the token is never returned.
    /// </summary>
    /// <remarks>
    ///     Refused when the person is deleted or restricted (409, AF-51c) or already verified (400,
    ///     AF-51d), and with 429 when an email of this kind went to them within the cooldown,
    ///     <c>HEIMDALL_ADMIN_EMAIL_COOLDOWN_IN_SECONDS</c> (five minutes by default, AF-51e). 200 means
    ///     the email was handed to the sender.
    /// </remarks>
    [HttpPost("persons/{id:guid}/verification-email")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<SendPersonVerificationEmailCommandOutput?>>> SendVerificationEmail(
        Guid id)
    {
        var command = new SendPersonVerificationEmailCommand { Id = id };
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<SendPersonVerificationEmailCommand, SendPersonVerificationEmailCommandOutput>(command);

        return result.ToActionResult(statusMap: PersonMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Has a password-reset link mailed to a person's own address (UC-52, FR-PR-05). A System Admin
    ///     may ask for any person; a Scope Admin only for a <c>User</c> of a scope they own (AF-52b). No
    ///     body; neither a token nor a password is accepted or returned.
    /// </summary>
    /// <remarks>
    ///     Eligibility is UC-12's: refused (409) when the person is deleted or restricted (AF-52c) or
    ///     their account could not sign in — a User whose scope is deleted, a Scope Admin with no live
    ///     scope (AF-52d). 429 when a reset went to them within the cooldown (AF-52e). Unlike UC-12 the
    ///     send is not queued: the caller is authenticated and owed a real answer. 200 means the email
    ///     was handed to the sender.
    /// </remarks>
    [HttpPost("persons/{id:guid}/password-reset")]
    [RoleRequirement((int)Roles.SystemAdmin, (int)Roles.ScopeAdmin)]
    public async Task<ActionResult<DataOutput<SendPersonPasswordResetCommandOutput?>>> SendPasswordReset(Guid id)
    {
        var command = new SendPersonPasswordResetCommand { Id = id };
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<SendPersonPasswordResetCommand, SendPersonPasswordResetCommandOutput>(command);

        return result.ToActionResult(statusMap: PersonMessageMap.StatusCodes);
    }
}
