using ArturRios.Heimdall.Shared.Security;
using ArturRios.Util.WebApi.Security.Extensions;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     <see cref="IActorAccessor" /> backed by the current request's <see cref="IdentityUser" />, the
///     same source <see cref="ActorExtensions.ApplyActor" /> reads. <c>null</c> on an anonymous
///     request (no authenticated user attached by <c>AuthenticationMiddleware</c>).
/// </summary>
public class HttpContextActorAccessor(IHttpContextAccessor httpContextAccessor) : IActorAccessor
{
    public Guid? ActorPersonId => httpContextAccessor.HttpContext?.GetUser<IdentityUser>()?.Id;

    public int? ActorRole => httpContextAccessor.HttpContext?.GetUser<IdentityUser>()?.RoleId;
}
