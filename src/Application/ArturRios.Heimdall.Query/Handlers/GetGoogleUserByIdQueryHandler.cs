using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>
///     Handles <see cref="GetGoogleUserByIdQuery" /> (UC-27, FR-GO-14): retrieves a Google User by its
///     <c>PublicId</c> within the addressed scope, excluding logically deleted records unless
///     explicitly requested (FR-GO-17), then applies the use case's visibility rule. A miss is AF-27a
///     (<c>GoogleUserNotFound</c>); a record the caller may not see is AF-27b
///     (<c>NotAuthorizedToViewGoogleUser</c>). Both are returned as errors rather than thrown.
/// </summary>
public class GetGoogleUserByIdQueryHandler(
    IAsyncReadOnlyRepository<GoogleUser> googleUserReader,
    IScopeOwnershipChecker scopeOwnership)
    : IQueryHandlerAsync<GetGoogleUserByIdQuery, GoogleUserOutput>
{
    /// <summary>
    ///     The Google User's payload plus the internal scope id the ownership rule needs. Internal ids
    ///     never reach the caller — only <see cref="Output" /> is returned (NFR-15).
    /// </summary>
    private sealed class GoogleUserProjection
    {
        public long ScopeInternalId { get; init; }

        public GoogleUserOutput Output { get; init; } = null!;
    }

    public async Task<DataOutput<GoogleUserOutput?>> HandleAsync(GetGoogleUserByIdQuery query)
    {
        var output = DataOutput<GoogleUserOutput?>.New;

        // The route's scopeId qualifies the lookup: a Google User that exists in another scope is not
        // the resource this path addresses, so it falls out here rather than at the rule below. The
        // same arrangement GetApplicationByIdQueryHandler uses.
        var googleUser = await googleUserReader.Query()
            .Where(x => x.PublicId == query.Id && x.Scope.PublicId == query.ScopeId &&
                        (query.IncludeDeleted || !x.IsDeleted))
            .Select(x => new GoogleUserProjection
            {
                ScopeInternalId = x.ScopeId,
                Output = new GoogleUserOutput
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
                }
            })
            .FirstOrDefaultAsync();

        // AF-27a: no such Google User under this scope (or it is logically deleted and was not
        // explicitly requested). Checked before authorization, so both alternative flows stay
        // observable — a GUID nobody holds cannot be told apart from one the caller may not see.
        if (googleUser is null)
        {
            return output.WithError(GoogleUserMessages.GoogleUserNotFound);
        }

        // AF-27b (UC-27 step 2). The use case grants three actors: a Google User reading themselves,
        // a System Admin reading anyone, and a Scope Admin reading the scopes they own. The last two
        // are exactly what IScopeOwnershipChecker decides, so the rule is the self-read plus that
        // check — which also inherits its guard that a logically deleted actor owns nothing. A
        // password User matches neither half, which is correct: the authorization matrix grants a
        // User this read only as self, and a person is never a Google User's self.
        if (query.ActingPersonId != googleUser.Output.Id &&
            !await scopeOwnership.ActorMayManageScopeAsync(
                query.ActingRole, query.ActingPersonId, googleUser.ScopeInternalId))
        {
            return output.WithError(GoogleUserMessages.NotAuthorizedToViewGoogleUser);
        }

        // UC-27 step 4.
        return output
            .WithData(googleUser.Output)
            .WithMessage(GoogleUserMessages.GoogleUserRetrievedSuccessfully);
    }
}
