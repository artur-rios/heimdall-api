using ArturRios.Util.Http;

namespace ArturRios.IdentityManager.Command.Messages;

/// <summary>
///     Maps each <see cref="ScopeMessages" /> value to the HTTP status code that should accompany it,
///     following the UC-01 alternative flows. Passed to the response resolver so it can pick the
///     status code from the output's first message (on success) or first error (on failure).
/// </summary>
public static class ScopeMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        // UC-01 main flow — scope created.
        [ScopeMessages.ScopeCreatedSuccessfully] = HttpStatusCodes.Created,
        // AF-01b — invalid input data, or no owner specified.
        [ScopeMessages.NameRequired] = HttpStatusCodes.BadRequest,
        [ScopeMessages.AtLeastOneOwnerRequired] = HttpStatusCodes.BadRequest,
        // AF-01a — scope name already exists.
        [ScopeMessages.NameAlreadyExists] = HttpStatusCodes.Conflict,
        // AF-01d — an owner is not an existing, non-deleted ScopeAdmin.
        [ScopeMessages.OwnerNotValidScopeAdmin] = HttpStatusCodes.BadRequest,
        // Not covered by the documentation: missing reference data is a server-side fault.
        [ScopeMessages.ScopeAdminRoleNotConfigured] = HttpStatusCodes.InternalServerError
    };
}
