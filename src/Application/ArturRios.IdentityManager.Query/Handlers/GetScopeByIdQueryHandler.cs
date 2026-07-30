using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Query.Handlers;

/// <summary>
///     Handles <see cref="GetScopeByIdQuery" /> (UC-02, FR-SC-02): retrieves a scope by its
///     <c>PublicId</c>, excluding logically deleted scopes unless explicitly requested (FR-SC-07),
///     then applies the use case's per-actor visibility rule. A missing scope is AF-02a
///     (<c>ScopeNotFound</c>); a scope the caller may not see is AF-02b
///     (<c>NotAuthorizedToViewScope</c>). Both are returned as errors rather than thrown.
/// </summary>
public class GetScopeByIdQueryHandler(IAsyncReadOnlyRepository<Scope> scopeReader)
    : IQueryHandlerAsync<GetScopeByIdQuery, ScopeOutput>
{
    /// <summary>
    ///     The scope plus the one membership fact the visibility rule needs and
    ///     <see cref="ScopeOutput" /> does not carry. Ownership needs no extra field: the output
    ///     already lists the owners' <c>PublicId</c>s.
    /// </summary>
    private sealed class ScopeProjection
    {
        public bool ActorBelongsToScope { get; init; }

        public ScopeOutput Output { get; init; } = null!;
    }

    public async Task<DataOutput<ScopeOutput?>> HandleAsync(GetScopeByIdQuery query)
    {
        var output = DataOutput<ScopeOutput?>.New;

        var scope = await scopeReader.Query()
            .Where(x => x.PublicId == query.Id && (query.IncludeDeleted || !x.IsDeleted))
            .Select(x => new ScopeProjection
            {
                ActorBelongsToScope = x.Users.Any(user => user.Person.PublicId == query.ActingPersonId),
                Output = new ScopeOutput
                {
                    Id = x.PublicId,
                    Name = x.Name,
                    Description = x.Description,
                    GoogleSignInEnabled = x.GoogleSignInEnabled,
                    IsDeleted = x.IsDeleted,
                    OwnerIds = x.Owners.Select(owner => owner.Person.PublicId).ToList(),
                    CreatedAt = x.CreatedAt,
                    UpdatedAt = x.UpdatedAt
                }
            })
            .FirstOrDefaultAsync();

        // AF-02a: no such scope (or it is logically deleted and was not explicitly requested).
        if (scope is null)
        {
            return output.WithError(ScopeMessages.ScopeNotFound);
        }

        // AF-02b: the caller is not allowed to see this scope.
        if (!MayView(query, scope))
        {
            return output.WithError(ScopeMessages.NotAuthorizedToViewScope);
        }

        return output
            .WithData(scope.Output)
            .WithMessage(ScopeMessages.ScopeRetrievedSuccessfully);
    }

    /// <summary>
    ///     UC-02 step 2: a System Admin sees every scope, a Scope Admin only the scopes they own, and
    ///     a User only the scope they belong to. Any other role is denied, so a role added later
    ///     cannot inherit read access to every scope by default.
    /// </summary>
    private static bool MayView(GetScopeByIdQuery query, ScopeProjection scope) => query.ActingRole switch
    {
        (int)Roles.SystemAdmin => true,
        (int)Roles.ScopeAdmin => scope.Output.OwnerIds.Contains(query.ActingPersonId),
        (int)Roles.User => scope.ActorBelongsToScope,
        _ => false
    };
}
