using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;

namespace ArturRios.IdentityManager.Query.Handlers;

/// <summary>
///     Handles <see cref="ListScopesQuery" /> (UC-02, FR-SC-03): lists scopes with pagination and an
///     optional name filter, excluding logically deleted scopes unless explicitly requested
///     (FR-SC-07).
/// </summary>
public class ListScopesQueryHandler(IAsyncReadOnlyRepository<Scope> scopeReader)
    : IPaginatedQueryHandlerAsync<ListScopesQuery, ScopeOutput>
{
    public async Task<PaginatedOutput<ScopeOutput>> HandleAsync(ListScopesQuery query)
    {
        var scopes = scopeReader.Query();

        if (!query.IncludeDeleted)
        {
            scopes = scopes.Where(x => !x.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var name = query.Name;
            scopes = scopes.Where(x => x.Name.Contains(name));
        }

        var projected = scopes.Select(x => new ScopeOutput
        {
            Id = x.PublicId,
            Name = x.Name,
            Description = x.Description,
            GoogleSignInEnabled = x.GoogleSignInEnabled,
            IsDeleted = x.IsDeleted,
            OwnerIds = x.Owners.Select(owner => owner.Person.PublicId).ToList(),
            CreatedAt = x.CreatedAt,
            UpdatedAt = x.UpdatedAt
        });

        var output = await projected.PaginateAsync(query.PageNumber, query.PageSize, x => x.Name);

        return output.WithMessage(ScopeMessages.ScopesRetrievedSuccessfully);
    }
}
