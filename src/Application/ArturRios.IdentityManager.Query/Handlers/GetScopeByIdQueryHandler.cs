using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Query.Handlers;

/// <summary>
///     Handles <see cref="GetScopeByIdQuery" /> (UC-02, FR-SC-02): retrieves a scope by its
///     <c>PublicId</c>, excluding logically deleted scopes unless explicitly requested (FR-SC-07).
///     A missing scope is returned as an error (AF-02a) rather than thrown.
/// </summary>
public class GetScopeByIdQueryHandler(IAsyncReadOnlyRepository<Scope> scopeReader)
    : IQueryHandlerAsync<GetScopeByIdQuery, ScopeOutput>
{
    public async Task<DataOutput<ScopeOutput?>> HandleAsync(GetScopeByIdQuery query)
    {
        var output = DataOutput<ScopeOutput?>.New;

        var scope = await scopeReader.Query()
            .Where(x => x.PublicId == query.Id && (query.IncludeDeleted || !x.IsDeleted))
            .Select(x => new ScopeOutput
            {
                Id = x.PublicId,
                Name = x.Name,
                Description = x.Description,
                GoogleSignInEnabled = x.GoogleSignInEnabled,
                IsDeleted = x.IsDeleted,
                OwnerIds = x.Owners.Select(owner => owner.Person.PublicId).ToList(),
                CreatedAt = x.CreatedAt,
                UpdatedAt = x.UpdatedAt
            })
            .FirstOrDefaultAsync();

        if (scope is null)
        {
            return output.WithError(ScopeMessages.ScopeNotFound);
        }

        return output
            .WithData(scope)
            .WithMessage(ScopeMessages.ScopeRetrievedSuccessfully);
    }
}
