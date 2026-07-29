using ArturRios.Util.Http;

namespace ArturRios.IdentityManager.Shared.Messages;

/// <summary>
///     Maps each <see cref="PersonMessages" /> value to its HTTP status code, following the UC-06 and
///     UC-07 flows. Passed to the response resolver.
/// </summary>
public static class PersonMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
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
        [PersonMessages.NotAuthorizedToViewPerson] = HttpStatusCodes.Forbidden
    };
}
