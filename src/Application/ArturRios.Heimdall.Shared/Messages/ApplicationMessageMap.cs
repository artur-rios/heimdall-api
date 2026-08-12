using ArturRios.Util.Http;

namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Maps each <see cref="ApplicationMessages" /> value to its HTTP status code, following the
///     UC-16 – UC-20 flows. Passed to the response resolver.
/// </summary>
public static class ApplicationMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        // UC-16 main flow — application created.
        [ApplicationMessages.ApplicationCreatedSuccessfully] = HttpStatusCodes.Created,
        // AF-16a — scope not found.
        [ApplicationMessages.ScopeNotFound] = HttpStatusCodes.NotFound,
        // AF-16e — the acting Scope Admin does not own the target scope; AF-16c — a Scope Admin
        // named someone else as owner.
        [ApplicationMessages.NotScopeOwner] = HttpStatusCodes.Forbidden,
        [ApplicationMessages.CannotSetAnotherOwner] = HttpStatusCodes.Forbidden,
        // AF-16b — the owner is not tied to the scope.
        [ApplicationMessages.OwnerNotValidForScope] = HttpStatusCodes.BadRequest,
        // AF-16d — invalid input.
        [ApplicationMessages.NameRequired] = HttpStatusCodes.BadRequest,
        [ApplicationMessages.NameTooLong] = HttpStatusCodes.BadRequest,
        [ApplicationMessages.OwnerRequired] = HttpStatusCodes.BadRequest,
        // UC-17 main flow — application(s) retrieved.
        [ApplicationMessages.ApplicationRetrievedSuccessfully] = HttpStatusCodes.Ok,
        [ApplicationMessages.ApplicationsRetrievedSuccessfully] = HttpStatusCodes.Ok,
        // AF-17a — application not found. The listing's own 404 reuses ScopeNotFound above.
        [ApplicationMessages.ApplicationNotFound] = HttpStatusCodes.NotFound,
        // AF-17b — caller may not view the application. The listing's 403 reuses NotScopeOwner.
        [ApplicationMessages.NotAuthorizedToViewApplication] = HttpStatusCodes.Forbidden,
        // UC-18 main flow — application updated.
        [ApplicationMessages.ApplicationUpdatedSuccessfully] = HttpStatusCodes.Ok,
        // AF-18c — caller may not update the application. AF-18a reuses ApplicationNotFound above,
        // AF-18b reuses OwnerNotValidForScope, and UC-18's input validation reuses the UC-16 messages.
        [ApplicationMessages.NotAuthorizedToUpdateApplication] = HttpStatusCodes.Forbidden,
        // UC-19 main flow — application logically deleted. AF-19b answers with the same 200, so the
        // response's AlreadyDeleted flag is what tells the two paths apart.
        [ApplicationMessages.ApplicationDeletedSuccessfully] = HttpStatusCodes.Ok,
        // AF-19c — caller may not delete the application. AF-19a reuses ApplicationNotFound above.
        [ApplicationMessages.NotAuthorizedToDeleteApplication] = HttpStatusCodes.Forbidden,
        // UC-20 main flow — application hard deleted. AF-20a reuses ApplicationNotFound above, and
        // UC-20's only other refusals are the framework's: 403 from [RoleRequirement], 401 unauthenticated.
        [ApplicationMessages.ApplicationHardDeletedSuccessfully] = HttpStatusCodes.Ok,
        // UC-17 listing (NFR-10) — invalid pagination or an over-length name filter.
        [PaginationMessages.InvalidPageNumber] = HttpStatusCodes.BadRequest,
        [PaginationMessages.InvalidPageSize] = HttpStatusCodes.BadRequest,
        [PaginationMessages.FilterTooLong] = HttpStatusCodes.BadRequest
    };
}
