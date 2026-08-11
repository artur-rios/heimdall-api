using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Builds the <see cref="AuthTokenSubject" /> UC-11 step 6 and UC-38 step 5 both need before
///     issuing the full authentication token — a <c>User</c> claims the scope they belong to, a
///     <c>ScopeAdmin</c> the live scopes they own, a <c>SystemAdmin</c> neither — and forwards to
///     <see cref="IAuthTokenIssuer" />. Shared so the scope-eligibility rules (AF-11d/AF-11e) and the
///     token-issuing call live in exactly one place, whichever use case is finishing the login:
///     <c>LoginCommandHandler</c> directly, or <c>VerifyTwoFactorAuthCommandHandler</c> after a
///     second factor checks out.
/// </summary>
public class PersonAuthTokenService(IAuthTokenIssuer tokenIssuer)
{
    /// <summary>
    ///     AF-11d/AF-11e: a <c>User</c> whose scope is logically deleted, or a <c>ScopeAdmin</c> with
    ///     no live owned scope, is not eligible for a token — <paramref name="subject" /> is
    ///     <c>null</c> and this returns <see langword="false" /> in that case. Requires
    ///     <paramref name="person" />'s <c>ScopeMembership.Scope</c> and <c>ScopeOwnerships.Scope</c>
    ///     navigations to already be loaded.
    /// </summary>
    public bool TryBuildSubject(Person person, out AuthTokenSubject? subject)
    {
        var liveOwnedScopeIds = person.ScopeOwnerships
            .Where(ownership => !ownership.Scope.IsDeleted)
            .Select(ownership => ownership.Scope.PublicId)
            .ToList();

        if (person.RoleId == (long)Roles.User && person.ScopeMembership!.Scope.IsDeleted)
        {
            subject = null;
            return false;
        }

        if (person.RoleId == (long)Roles.ScopeAdmin && liveOwnedScopeIds.Count == 0)
        {
            subject = null;
            return false;
        }

        subject = new AuthTokenSubject(
            person.PublicId,
            (int)person.RoleId,
            person.RoleId == (long)Roles.User ? person.ScopeMembership!.Scope.PublicId : null,
            liveOwnedScopeIds);

        return true;
    }

    /// <summary>Issues the full authentication token for a subject already built by <see cref="TryBuildSubject" />.</summary>
    public Task<AuthToken> IssueAsync(AuthTokenSubject subject) => tokenIssuer.IssueAsync(subject);
}
