using ArturRios.Util.Http;

namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Maps each <see cref="ScopePermissionMessages" /> value to its HTTP status code, following the
///     UC-31 – UC-35 flows. Passed to the response resolver. The single <see cref="ScopePermissionMessages.NotScopeOwner" />
///     message covers the scope-ownership refusal for create, retrieve, update, and delete: a scope
///     permission has no owner of its own, so owning the scope is the only authorization there is.
///     UC-35's only refusals are the framework's: 403 from [RoleRequirement], 401 unauthenticated.
/// </summary>
public static class ScopePermissionMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        // UC-31 main flow — scope permission created.
        [ScopePermissionMessages.ScopePermissionCreatedSuccessfully] = HttpStatusCodes.Created,
        // AF-31a — scope not found.
        [ScopePermissionMessages.ScopeNotFound] = HttpStatusCodes.NotFound,
        // AF-31e / AF-32e / AF-33e / AF-34e — acting Scope Admin does not own the target scope.
        [ScopePermissionMessages.NotScopeOwner] = HttpStatusCodes.Forbidden,
        // AF-31d — invalid input.
        [ScopePermissionMessages.NameRequired] = HttpStatusCodes.BadRequest,
        [ScopePermissionMessages.NameTooLong] = HttpStatusCodes.BadRequest,
        [ScopePermissionMessages.DescriptionTooLong] = HttpStatusCodes.BadRequest,
        // UC-32 main flow — scope permission(s) retrieved.
        [ScopePermissionMessages.ScopePermissionRetrievedSuccessfully] = HttpStatusCodes.Ok,
        [ScopePermissionMessages.ScopePermissionsRetrievedSuccessfully] = HttpStatusCodes.Ok,
        // AF-32a — scope permission not found. The listing's own 404 reuses ScopeNotFound above.
        [ScopePermissionMessages.ScopePermissionNotFound] = HttpStatusCodes.NotFound,
        // UC-33 main flow — scope permission updated. AF-33a reuses ScopePermissionNotFound, and
        // UC-33's input validation reuses the UC-31 messages.
        [ScopePermissionMessages.ScopePermissionUpdatedSuccessfully] = HttpStatusCodes.Ok,
        // UC-34 main flow — scope permission logically deleted. AF-34b answers with the same 200, so
        // the response's AlreadyDeleted flag is what tells the two paths apart. AF-34a reuses
        // ScopePermissionNotFound, and AF-34e reuses NotScopeOwner.
        [ScopePermissionMessages.ScopePermissionDeletedSuccessfully] = HttpStatusCodes.Ok,
        // UC-35 main flow — scope permission hard deleted. AF-35a reuses ScopePermissionNotFound, and
        // UC-35's only other refusals are the framework's: 403 from [RoleRequirement], 401
        // unauthenticated.
        [ScopePermissionMessages.ScopePermissionHardDeletedSuccessfully] = HttpStatusCodes.Ok,
        // UC-32 read b (NFR-10) — invalid pagination or an over-length name filter.
        [PaginationMessages.InvalidPageNumber] = HttpStatusCodes.BadRequest,
        [PaginationMessages.InvalidPageSize] = HttpStatusCodes.BadRequest,
        [PaginationMessages.FilterTooLong] = HttpStatusCodes.BadRequest
    };
}