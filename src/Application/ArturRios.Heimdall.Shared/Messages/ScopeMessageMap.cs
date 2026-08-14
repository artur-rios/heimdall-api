using ArturRios.Util.Http;

namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Maps each <see cref="ScopeMessages" /> value to the HTTP status code that should accompany it,
///     following the UC-01 / UC-02 flows. Passed to the response resolver so it can pick the status
///     code from the output's first message (on success) or first error (on failure).
/// </summary>
public static class ScopeMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes =
        DataAccessMessageMap.CombinedWith(new Dictionary<string, int>
        {
            // UC-01 main flow — scope created.
            [ScopeMessages.ScopeCreatedSuccessfully] = HttpStatusCodes.Created,
            // UC-03 main flow — scope updated.
            [ScopeMessages.ScopeUpdatedSuccessfully] = HttpStatusCodes.Ok,
            // UC-04 main flow (and AF-04b idempotent) — scope deleted.
            [ScopeMessages.ScopeDeletedSuccessfully] = HttpStatusCodes.Ok,
            // UC-05 main flow — scope hard deleted.
            [ScopeMessages.ScopeHardDeletedSuccessfully] = HttpStatusCodes.Ok,
            // UC-02 main flow — scope(s) retrieved.
            [ScopeMessages.ScopeRetrievedSuccessfully] = HttpStatusCodes.Ok,
            [ScopeMessages.ScopesRetrievedSuccessfully] = HttpStatusCodes.Ok,
            // AF-02a — scope not found.
            [ScopeMessages.ScopeNotFound] = HttpStatusCodes.NotFound,
            // AF-02b — caller may not view this scope.
            [ScopeMessages.NotAuthorizedToViewScope] = HttpStatusCodes.Forbidden,
            // AF-01b — invalid input data, or no owner specified.
            [ScopeMessages.NameRequired] = HttpStatusCodes.BadRequest,
            [ScopeMessages.AtLeastOneOwnerRequired] = HttpStatusCodes.BadRequest,
            // AF-01a — scope name already exists.
            [ScopeMessages.NameAlreadyExists] = HttpStatusCodes.Conflict,
            // AF-01d — an owner is not an existing, non-deleted ScopeAdmin.
            [ScopeMessages.OwnerNotValidScopeAdmin] = HttpStatusCodes.BadRequest,
            // UC-24 main flow — Google Sign-In enabled or disabled. AF-24a reuses ScopeNotFound (404).
            [ScopeMessages.GoogleSignInUpdatedSuccessfully] = HttpStatusCodes.Ok,
            // AF-24b — actor is not an owner of the target scope.
            [ScopeMessages.NotScopeOwner] = HttpStatusCodes.Forbidden,
            // UC-24 (NFR-10) — the request omitted the value to set.
            [ScopeMessages.EnabledRequired] = HttpStatusCodes.BadRequest,
            // UC-02 read b (NFR-10) — invalid pagination or an over-length name filter.
            [PaginationMessages.InvalidPageNumber] = HttpStatusCodes.BadRequest,
            [PaginationMessages.InvalidPageSize] = HttpStatusCodes.BadRequest,
            [PaginationMessages.FilterTooLong] = HttpStatusCodes.BadRequest
        });
}
