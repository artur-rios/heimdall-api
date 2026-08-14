using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.WebApi.Security.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     Global authorization filter rejecting a request whose bearer token names an identity that no
///     longer exists or has been logically deleted (FR-AU-05, FR-GO-12), or whose role claim no
///     longer matches that identity's role (Threat Model TH-08), with <c>401 Unauthorized</c>.
/// </summary>
/// <remarks>
///     <para>
///         Authentication runs in <c>ClaimsOnly</c> mode (<c>Startup.ConfigureSecurity</c>): the
///         caller is rebuilt from the token's claims and no data store is consulted, which is what
///         keeps an ordinary request free of a per-request lookup. The cost is that a token outlives
///         the state it describes — both the identity it names and the role it claims, for the whole
///         of its lifetime, an hour by default — and nothing in the pipeline noticed.
///     </para>
///     <para>
///         Several handlers already compensated, and that is precisely why this filter exists:
///         <c>ScopeOwnershipChecker</c> and <c>GetPersonByIdQueryHandler</c> excluded a deleted actor
///         explicitly, while the checks that grant on the role claim alone — every System Admin
///         bypass, and every "the actor is acting on themselves" branch — did not. The protection was
///         therefore present for a Scope Admin and absent for a System Admin, who can do strictly
///         more. Enforcing it once here makes the rule uniform instead of a per-handler decision that
///         each new endpoint has to remember, and the handlers' own guards stay as defence in depth.
///     </para>
///     <para>
///         Both identity tables are consulted because both mint tokens through the same claims: a
///         password identity is a <see cref="Person" /> (UC-11), a Google-authenticated one is a
///         <see cref="GoogleUser" /> (UC-25 step 8), and the token itself does not say which. The
///         lookups run against unique indexes on <c>public_id</c>, and the second only when the first
///         misses — so an authenticated request costs one indexed read, and a Google User's two.
///     </para>
///     <para>
///         A request carrying no bearer token resolves to <c>null</c> and is left alone, exactly as
///         <see cref="MfaPendingGuardFilter" /> leaves it: this filter narrows an identity the
///         pipeline already attached and never requires authentication of its own, so
///         <c>[AllowAnonymous]</c> endpoints are unaffected.
///     </para>
/// </remarks>
public class ActorLivenessFilter(
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncReadOnlyRepository<GoogleUser> googleUserReader) : IAsyncAuthorizationFilter
{
    /// <summary>
    ///     The single refusal, deliberately identical for "no such identity" and "deleted": a caller
    ///     holding a token the API will not honour learns only that, never which of the two it was.
    /// </summary>
    public const string ActorNotLive = "The identity this token names no longer exists or has been deleted.";

    /// <summary>
    ///     The refusal for a token whose role claim no longer matches the identity's role. Separate
    ///     from <see cref="ActorNotLive" /> on purpose: that message is deliberately vague because
    ///     the two cases it covers are both questions about whether an account exists, which a
    ///     caller has no business learning. A role change is not an existence question — it is the
    ///     caller's own account, they can establish it by signing in again, and telling them to is
    ///     more useful than telling them nothing.
    /// </summary>
    public const string ActorRoleChanged = "This token's role is out of date. Sign in again.";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.GetUser<IdentityUser>();

        if (user is null)
        {
            return;
        }

        var error = await RefusalReasonAsync(user.Id, user.RoleId);

        if (error is null)
        {
            return;
        }

        var output = ProcessOutput.New.WithError(error);

        context.Result = new ObjectResult(output) { StatusCode = HttpStatusCodes.Unauthorized };
    }

    /// <summary>
    ///     Why this actor may not act, or <c>null</c> when it may.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The role is compared as well as the identity, and the reason is TH-08 in the Threat
    ///         Model: without this, a role change took effect only when the token expired — an hour
    ///         by default — because the role travels in a claim and nothing re-read it. Deletion was
    ///         enforced within one request and demotion was not enforced at all.
    ///     </para>
    ///     <para>
    ///         That gap was worse than a stale privilege, because the window was long enough to make
    ///         itself permanent: a role change is authorised from the acting role claim, and the one
    ///         transition <c>UpdatePersonCommandHandler</c> supports is a promotion to System Admin,
    ///         so a demoted System Admin could promote themselves back with a write that outlives
    ///         every token involved.
    ///     </para>
    ///     <para>
    ///         It costs nothing: the row was already being read to check liveness, so this reads the
    ///         role from that same row instead of asking whether it exists.
    ///     </para>
    /// </remarks>
    private async Task<string?> RefusalReasonAsync(Guid actorPublicId, int actorRole)
    {
        // Nullable so "no live person with this id" is distinguishable from a role value. No role is
        // zero — Roles runs from 1 — but relying on that would be relying on an enum's numbering.
        var personRole = await personReader.Query()
            .Where(person => person.PublicId == actorPublicId && !person.IsDeleted)
            .Select(person => (long?)person.RoleId)
            .FirstOrDefaultAsync();

        if (personRole is not null)
        {
            return personRole == actorRole ? null : ActorRoleChanged;
        }

        var googleUserIsLive = await googleUserReader.Query()
            .AnyAsync(googleUser => googleUser.PublicId == actorPublicId && !googleUser.IsDeleted);

        if (!googleUserIsLive)
        {
            return ActorNotLive;
        }

        // A Google User has no stored role — authentication is delegated to Google — and UC-25 mints
        // its token with Roles.User every time. So there is no drift to detect here, only the
        // assertion that a token naming a Google identity claims nothing more than that role.
        return actorRole == (int)Roles.User ? null : ActorRoleChanged;
    }
}
