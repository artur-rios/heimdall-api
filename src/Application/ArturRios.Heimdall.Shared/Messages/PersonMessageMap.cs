using ArturRios.Util.Http;

namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Maps each <see cref="PersonMessages" /> value to its HTTP status code, following the UC-06,
///     UC-07, UC-08, UC-09 and UC-10 flows. Passed to the response resolver.
/// </summary>
public static class PersonMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes =
        DataAccessMessageMap.CombinedWith(new Dictionary<string, int>
        {
            // UC-06 main flow — person created.
            [PersonMessages.PersonCreatedSuccessfully] = HttpStatusCodes.Created,
            // AF-06a — email already exists.
            [PersonMessages.EmailAlreadyExists] = HttpStatusCodes.Conflict,
            // AF-06b — scope not found.
            [PersonMessages.ScopeNotFound] = HttpStatusCodes.NotFound,
            // AF-06e — actor is not an owner of the target scope.
            [PersonMessages.NotScopeOwner] = HttpStatusCodes.Forbidden,
            // AF-06d — invalid input.
            [PersonMessages.NameRequired] = HttpStatusCodes.BadRequest,
            [PersonMessages.NameTooLong] = HttpStatusCodes.BadRequest,
            [PersonMessages.EmailRequired] = HttpStatusCodes.BadRequest,
            [PersonMessages.EmailInvalid] = HttpStatusCodes.BadRequest,
            [PersonMessages.PasswordRequired] = HttpStatusCodes.BadRequest,
            [PersonMessages.PasswordTooShort] = HttpStatusCodes.BadRequest,
            [PersonMessages.InvalidRole] = HttpStatusCodes.BadRequest,
            // UC-07 main flow — person(s) retrieved.
            [PersonMessages.PersonRetrievedSuccessfully] = HttpStatusCodes.Ok,
            [PersonMessages.PersonsRetrievedSuccessfully] = HttpStatusCodes.Ok,
            // AF-07a — person not found.
            [PersonMessages.PersonNotFound] = HttpStatusCodes.NotFound,
            // AF-07b — caller may not view the person.
            [PersonMessages.NotAuthorizedToViewPerson] = HttpStatusCodes.Forbidden,
            // UC-08 main flow — person updated.
            [PersonMessages.PersonUpdatedSuccessfully] = HttpStatusCodes.Ok,
            // UC-08 — caller may not update the person; AF-08c for the role-change case.
            [PersonMessages.NotAuthorizedToUpdatePerson] = HttpStatusCodes.Forbidden,
            [PersonMessages.RoleChangeRequiresSystemAdmin] = HttpStatusCodes.Forbidden,
            // UC-08 — the transition needs a scope the request does not carry, or the role is unknown.
            [PersonMessages.UnsupportedRoleTransition] = HttpStatusCodes.BadRequest,
            [PersonMessages.UnknownRole] = HttpStatusCodes.BadRequest,
            // NFR-12 — the change would strip a scope of its last owner (UC-08 role change, UC-09 AF-09e).
            [PersonMessages.ScopeWouldLoseLastOwner] = HttpStatusCodes.Conflict,
            // UC-09 main flow and AF-09b — person deleted, or already was.
            [PersonMessages.PersonDeletedSuccessfully] = HttpStatusCodes.Ok,
            // AF-09c — caller may not delete the person; AF-09d / AF-10c — caller targeted themselves.
            [PersonMessages.NotAuthorizedToDeletePerson] = HttpStatusCodes.Forbidden,
            [PersonMessages.CannotDeleteSelf] = HttpStatusCodes.Forbidden,
            // UC-10 main flow — person hard deleted. AF-10a reuses PersonNotFound (404), AF-10b reuses
            // ScopeWouldLoseLastOwner (409), and AF-10c reuses CannotDeleteSelf (403).
            [PersonMessages.PersonHardDeletedSuccessfully] = HttpStatusCodes.Ok,
            // UC-21 main flow — ownership added. AF-21d answers 200 instead, so the two paths carry
            // different messages; AF-21a reuses ScopeNotFound (404) and AF-21c NotScopeOwner (403).
            [PersonMessages.ScopeOwnerAddedSuccessfully] = HttpStatusCodes.Created,
            [PersonMessages.AlreadyScopeOwner] = HttpStatusCodes.Ok,
            // AF-21b — the named person is not a usable ScopeAdmin.
            [PersonMessages.PersonNotValidScopeAdmin] = HttpStatusCodes.BadRequest,
            // UC-22 main flow — ownership removed. AF-22a reuses ScopeNotFound (404) for its scope half,
            // AF-22b reuses ScopeWouldLoseLastOwner (409) and AF-22c NotScopeOwner (403).
            [PersonMessages.ScopeOwnerRemovedSuccessfully] = HttpStatusCodes.Ok,
            // AF-22a — the named person holds no ownership row for this scope.
            [PersonMessages.PersonNotScopeOwner] = HttpStatusCodes.NotFound,
            // UC-23 main flow — the User was promoted to owner. AF-23a reuses ScopeNotFound (404),
            // AF-23c NotScopeOwner (403), and the FR-PE-09 namespace guard EmailAlreadyExists (409).
            [PersonMessages.ScopeUserPromotedSuccessfully] = HttpStatusCodes.Ok,
            // AF-23b — the named person is not a usable User of the target scope.
            [PersonMessages.PersonNotScopeUser] = HttpStatusCodes.BadRequest,
            // AF-23d — the person is already a ScopeAdmin.
            [PersonMessages.AlreadyScopeAdmin] = HttpStatusCodes.Conflict,
            // UC-07 reads b/c (NFR-10) — invalid pagination or an over-length name/email filter.
            [PaginationMessages.InvalidPageNumber] = HttpStatusCodes.BadRequest,
            [PaginationMessages.InvalidPageSize] = HttpStatusCodes.BadRequest,
            [PaginationMessages.FilterTooLong] = HttpStatusCodes.BadRequest
        });
}
