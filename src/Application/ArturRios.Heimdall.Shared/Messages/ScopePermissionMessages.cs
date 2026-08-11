namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Canonical messages produced by the scope-permission use cases (UC-31 – UC-35). Each is mapped
///     to an HTTP status code in <see cref="ScopePermissionMessageMap" />. A scope permission is a
///     scope-child resource — it carries no separate owner of its own — so authorization is the
///     scope-ownership check, and a single <see cref="NotScopeOwner" /> covers create, retrieve,
///     update, and delete. UC-35's only actor is the System Admin, settled entirely by the
///     endpoint's role requirement, so its single refusal message is the framework's 403.
/// </summary>
public static class ScopePermissionMessages
{
    /// <summary>UC-31 success: the scope permission was created.</summary>
    public const string ScopePermissionCreatedSuccessfully = "Scope permission created successfully.";

    /// <summary>AF-31a: the target scope does not exist or is logically deleted.</summary>
    public const string ScopeNotFound = "Scope not found.";

    /// <summary>
    ///     AF-31e / AF-32e / AF-33e / AF-34e: the caller is not a System Admin and does not own the
    ///     target scope, so they may not manage or retrieve the scope's permissions.
    /// </summary>
    public const string NotScopeOwner = "You are not an owner of the target scope.";

    /// <summary>AF-31d: the permission name was not supplied.</summary>
    public const string NameRequired = "Scope permission name is required.";

    /// <summary>AF-31d: the permission name exceeds the maximum length.</summary>
    public const string NameTooLong = "Scope permission name must be at most 200 characters.";

    /// <summary>AF-31d: the permission description exceeds the maximum length.</summary>
    public const string DescriptionTooLong = "Scope permission description must be at most 500 characters.";

    /// <summary>UC-32 success: a single scope permission was retrieved.</summary>
    public const string ScopePermissionRetrievedSuccessfully = "Scope permission retrieved successfully.";

    /// <summary>UC-32 success: a list of scope permissions was retrieved.</summary>
    public const string ScopePermissionsRetrievedSuccessfully = "Scope permissions retrieved successfully.";

    /// <summary>
    ///     AF-32a: no scope permission holds that identifier inside the addressed scope — or it is
    ///     logically deleted and was not explicitly requested.
    /// </summary>
    public const string ScopePermissionNotFound = "Scope permission not found.";

    /// <summary>UC-33 success: the scope permission was updated.</summary>
    public const string ScopePermissionUpdatedSuccessfully = "Scope permission updated successfully.";

    /// <summary>
    ///     UC-34 success: the scope permission is logically deleted. Also carries the idempotent
    ///     AF-34b path, where the permission was already deleted and nothing was written.
    /// </summary>
    public const string ScopePermissionDeletedSuccessfully = "Scope permission deleted successfully.";

    /// <summary>UC-35 success: the scope permission was permanently (hard) deleted.</summary>
    public const string ScopePermissionHardDeletedSuccessfully = "Scope permission hard deleted successfully.";
}