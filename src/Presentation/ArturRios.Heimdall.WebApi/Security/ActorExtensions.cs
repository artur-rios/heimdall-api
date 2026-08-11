using ArturRios.Heimdall.Shared.Security;
using ArturRios.Util.WebApi.Security.Extensions;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     Bridges the authenticated caller on the request to the actor-scoped commands and queries whose
///     authorization depends on who is acting.
/// </summary>
public static class ActorExtensions
{
    /// <summary>
    ///     Copies the authenticated caller (attached to the request by the auth middleware) onto an
    ///     actor-scoped command or query, so the handler can enforce scope-scoped authorization
    ///     (UC-02 AF-02b, UC-06 AF-06e, UC-07 AF-07b). The acting fields are always taken from the
    ///     token, never from the request.
    /// </summary>
    public static void ApplyActor(this HttpContext httpContext, IActorScoped actorScoped)
    {
        var actor = httpContext.GetUser<IdentityUser>()!;

        actorScoped.ActingPersonId = actor.Id;
        actorScoped.ActingRole = actor.RoleId;
    }
}
