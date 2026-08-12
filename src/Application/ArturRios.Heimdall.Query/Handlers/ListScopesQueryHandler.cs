using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>
///     Handles <see cref="ListScopesQuery" /> (UC-02, FR-SC-03): lists scopes with pagination and an
///     optional name filter, excluding logically deleted scopes unless explicitly requested
///     (FR-SC-07).
/// </summary>
public class ListScopesQueryHandler(
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IValidator<ListScopesQuery> validator)
    : IPaginatedQueryHandlerAsync<ListScopesQuery, ScopeOutput>
{
    public async Task<PaginatedOutput<ScopeOutput>> HandleAsync(ListScopesQuery query)
    {
        // NFR-10: page number/size bounds and filter length, validated before any query runs.
        var validation = await validator.ValidateAsync(query);

        if (!validation.IsValid)
        {
            return PaginatedOutput<ScopeOutput>.New
                .WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        var scopes = scopeReader.Query();

        if (!query.IncludeDeleted)
        {
            scopes = scopes.Where(x => !x.IsDeleted);
        }

        // Compared case-insensitively (LOWER() in SQL), as the person listings do (FR-PE-04) and as
        // every name/email comparison in this codebase does.
        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var name = query.Name.ToLower();
            scopes = scopes.Where(x => x.Name.ToLower().Contains(name));
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
