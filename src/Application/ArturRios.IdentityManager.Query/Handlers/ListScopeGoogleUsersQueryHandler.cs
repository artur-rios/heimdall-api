using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Query.Handlers;

/// <summary>
///     Handles <see cref="ListScopeGoogleUsersQuery" /> (UC-27, FR-GO-14): lists the Google Users of a
///     scope with pagination and optional name/email filters, excluding logically deleted records
///     unless explicitly requested (FR-GO-17). A missing or logically deleted scope is AF-27a
///     (<c>ScopeNotFound</c>); an actor who does not own the scope is AF-27b (<c>NotScopeOwner</c>).
///     A System Admin bypasses the ownership check.
/// </summary>
/// <remarks>
///     The counterpart of <see cref="GetGoogleUserByIdQueryHandler" />, and deliberately stricter: the
///     authorization matrix grants a Google User a read of *themselves*, never a listing, so there is
///     no self-read branch here. The <c>RoleRequirement</c> on the endpoint keeps every <c>User</c>
///     out before this runs, and this check answers for the Scope Admin the attribute cannot judge.
/// </remarks>
public class ListScopeGoogleUsersQueryHandler(
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncReadOnlyRepository<GoogleUser> googleUserReader,
    IScopeOwnershipChecker scopeOwnership)
    : IPaginatedQueryHandlerAsync<ListScopeGoogleUsersQuery, GoogleUserOutput>
{
    public async Task<PaginatedOutput<GoogleUserOutput>> HandleAsync(ListScopeGoogleUsersQuery query)
    {
        var output = PaginatedOutput<GoogleUserOutput>.New;

        // AF-27a: the target scope must exist and not be logically deleted.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == query.ScopeId && !x.IsDeleted);

        if (scope is null)
        {
            return output.WithError(GoogleUserMessages.ScopeNotFound);
        }

        // AF-27b: a Scope Admin may only read a scope they own; a System Admin bypasses.
        if (!await scopeOwnership.ActorMayManageScopeAsync(query.ActingRole, query.ActingPersonId, scope.Id))
        {
            return output.WithError(GoogleUserMessages.NotScopeOwner);
        }

        var googleUsers = googleUserReader.Query().Where(x => x.ScopeId == scope.Id);

        // UC-27 step 3 (FR-GO-17).
        if (!query.IncludeDeleted)
        {
            googleUsers = googleUsers.Where(x => !x.IsDeleted);
        }

        // FR-GO-14's filtering, in the vocabulary ListScopePersonsQueryHandler already uses:
        // case-insensitive substring matches, which translate to LOWER() … LIKE in SQL.
        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var name = query.Name.ToLower();
            googleUsers = googleUsers.Where(x => x.Name.ToLower().Contains(name));
        }

        if (!string.IsNullOrWhiteSpace(query.Email))
        {
            var email = query.Email.ToLower();
            googleUsers = googleUsers.Where(x => x.Email.ToLower().Contains(email));
        }

        var projected = googleUsers.Select(x => new GoogleUserOutput
        {
            Id = x.PublicId,
            GoogleId = x.GoogleId,
            Name = x.Name,
            Email = x.Email,
            EmailVerified = x.EmailVerified,
            ProfilePictureUrl = x.ProfilePictureUrl,
            IsDeleted = x.IsDeleted,
            ScopeId = x.Scope.PublicId,
            CreatedAt = x.CreatedAt,
            UpdatedAt = x.UpdatedAt
        });

        var page = await projected.PaginateAsync(query.PageNumber, query.PageSize, x => x.Name);

        // UC-27 step 4.
        return page.WithMessage(GoogleUserMessages.GoogleUsersRetrievedSuccessfully);
    }
}
