namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Canonical messages produced by the scope use cases (commands and queries). Every message that
///     reaches a caller is declared here so it can be validated against and mapped to an HTTP status
///     code in <see cref="ScopeMessageMap" />.
/// </summary>
public static class ScopeMessages
{
    /// <summary>UC-01 success: the scope was created.</summary>
    public const string ScopeCreatedSuccessfully = "Scope created successfully.";

    /// <summary>UC-03 success: the scope was updated.</summary>
    public const string ScopeUpdatedSuccessfully = "Scope updated successfully.";

    /// <summary>UC-04 success: the scope was logically deleted (also used for the AF-04b idempotent no-op).</summary>
    public const string ScopeDeletedSuccessfully = "Scope deleted successfully.";

    /// <summary>UC-05 success: the scope was permanently (hard) deleted.</summary>
    public const string ScopeHardDeletedSuccessfully = "Scope hard deleted successfully.";

    /// <summary>UC-02 success: a single scope was retrieved.</summary>
    public const string ScopeRetrievedSuccessfully = "Scope retrieved successfully.";

    /// <summary>UC-02 success: a list of scopes was retrieved.</summary>
    public const string ScopesRetrievedSuccessfully = "Scopes retrieved successfully.";

    /// <summary>AF-02a: the requested scope does not exist (or is logically deleted and not requested).</summary>
    public const string ScopeNotFound = "Scope not found.";

    /// <summary>AF-02b: the caller is not allowed to view the requested scope.</summary>
    public const string NotAuthorizedToViewScope = "You are not allowed to view this scope.";

    /// <summary>AF-01b: the scope name was not supplied.</summary>
    public const string NameRequired = "Scope name is required.";

    /// <summary>AF-01b: no owner was supplied.</summary>
    public const string AtLeastOneOwnerRequired = "At least one owner must be specified.";

    /// <summary>AF-01a: a scope with the requested name already exists.</summary>
    public const string NameAlreadyExists = "A scope with this name already exists.";

    /// <summary>AF-01d: an owner does not reference an existing, non-deleted ScopeAdmin.</summary>
    public const string OwnerNotValidScopeAdmin =
        "One or more owners do not reference an existing, non-deleted ScopeAdmin.";

    /// <summary>UC-24 success: the scope's Google Sign-In setting was changed.</summary>
    public const string GoogleSignInUpdatedSuccessfully = "Google Sign-In setting updated successfully.";

    /// <summary>
    ///     AF-24b: a Scope Admin acted on a scope they do not own. Same wording as the
    ///     <c>NotScopeOwner</c> of <see cref="PersonMessages" /> and <see cref="ApplicationMessages" />
    ///     so every controller answers an unowned scope identically.
    /// </summary>
    public const string NotScopeOwner = "You are not an owner of the target scope.";

    /// <summary>UC-24 (NFR-10): the Google Sign-In request did not say which value to set.</summary>
    public const string EnabledRequired = "Enabled is required.";
}
