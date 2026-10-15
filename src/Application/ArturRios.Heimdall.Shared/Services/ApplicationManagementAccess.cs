using ArturRios.Heimdall.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Shared.Services;

/// <summary>The outcome of <see cref="ApplicationManagementAccess.ResolveAsync" />.</summary>
public enum ApplicationAccess
{
    /// <summary>The actor may manage the application.</summary>
    Granted,

    /// <summary>No live application with that id lives in the addressed, live scope.</summary>
    NotFound,

    /// <summary>The application exists, but the actor may not manage it.</summary>
    Forbidden
}

/// <summary>The resolved application — only when <see cref="Access" /> is <see cref="ApplicationAccess.Granted" />.</summary>
public sealed record ApplicationAccessResult(ApplicationAccess Access, Application? Application);

/// <summary>
///     Who may manage an application's credentials (UC-53…UC-55, FR-AP-12): the rule UC-17, UC-18
///     and UC-19 already apply to the application itself.
/// </summary>
/// <remarks>
///     <para>
///         A System Admin manages any application. A Scope Admin manages one only when they are its
///         owner <em>and</em> still own its scope: owning the scope is not grounds to manage another
///         owner's application, and an owner UC-22 removed from the scope keeps nothing (AF-17b).
///     </para>
///     <para>
///         Not found is answered before authorization, as UC-17 does, so a missing application and
///         one the actor may not touch stay distinguishable to the people allowed to see either. A
///         logically deleted application, or one whose scope is logically deleted, is not found:
///         its credentials are inert (UC-56, UC-57), so there is nothing to manage.
///     </para>
/// </remarks>
public static class ApplicationManagementAccess
{
    public static async Task<ApplicationAccessResult> ResolveAsync(
        IQueryable<Application> applications,
        Guid scopeId,
        Guid applicationId,
        int actingRole,
        Guid actingPersonId)
    {
        var application = await applications
            .Include(x => x.Scope)
            .Include(x => x.Owner)
            .ThenInclude(owner => owner.ScopeOwnerships)
            .FirstOrDefaultAsync(x => x.PublicId == applicationId && x.Scope.PublicId == scopeId &&
                                      !x.IsDeleted && !x.Scope.IsDeleted);

        if (application is null)
        {
            return new ApplicationAccessResult(ApplicationAccess.NotFound, null);
        }

        if (actingRole == (int)Roles.SystemAdmin)
        {
            return new ApplicationAccessResult(ApplicationAccess.Granted, application);
        }

        var ownsApplicationAndScope =
            application.Owner.PublicId == actingPersonId &&
            application.Owner.ScopeOwnerships.Any(ownership => ownership.ScopeId == application.ScopeId);

        return ownsApplicationAndScope
            ? new ApplicationAccessResult(ApplicationAccess.Granted, application)
            : new ApplicationAccessResult(ApplicationAccess.Forbidden, null);
    }
}
