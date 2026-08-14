using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.WebApi.Security.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     Global authorization filter rejecting a request whose bearer token names an identity that no
///     longer exists or has been logically deleted (FR-AU-05, FR-GO-12), with
///     <c>401 Unauthorized</c>.
/// </summary>
/// <remarks>
///     <para>
///         Authentication runs in <c>ClaimsOnly</c> mode (<c>Startup.ConfigureSecurity</c>): the
///         caller is rebuilt from the token's claims and no data store is consulted, which is what
///         keeps an ordinary request free of a per-request lookup. The cost is that a token outlives
///         the identity it names — for the whole of its lifetime, an hour by default — and nothing in
///         the pipeline noticed.
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

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.GetUser<IdentityUser>();

        if (user is null)
        {
            return;
        }

        if (await IsLiveAsync(user.Id))
        {
            return;
        }

        var output = ProcessOutput.New.WithError(ActorNotLive);

        context.Result = new ObjectResult(output) { StatusCode = HttpStatusCodes.Unauthorized };
    }

    private async Task<bool> IsLiveAsync(Guid actorPublicId) =>
        await personReader.Query().AnyAsync(person => person.PublicId == actorPublicId && !person.IsDeleted) ||
        await googleUserReader.Query()
            .AnyAsync(googleUser => googleUser.PublicId == actorPublicId && !googleUser.IsDeleted);
}
