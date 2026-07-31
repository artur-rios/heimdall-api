namespace ArturRios.IdentityManager.Shared.Messages;

/// <summary>
///     Canonical messages produced by the application use cases. Each is mapped to an HTTP status code
///     in <see cref="ApplicationMessageMap" />.
/// </summary>
public static class ApplicationMessages
{
    /// <summary>UC-16 success: the application was created.</summary>
    public const string ApplicationCreatedSuccessfully = "Application created successfully.";

    /// <summary>AF-16a: the target scope does not exist or is logically deleted.</summary>
    public const string ScopeNotFound = "Scope not found.";

    /// <summary>UC-16: a Scope Admin acted on a scope they do not own.</summary>
    public const string NotScopeOwner = "You are not an owner of the target scope.";

    /// <summary>AF-16c: a User named someone other than themself as the owner.</summary>
    public const string CannotSetAnotherOwner = "You may only create applications you own.";

    /// <summary>
    ///     AF-16b: the owner does not exist, is logically deleted, or is neither a <c>User</c> of the
    ///     scope nor a <c>ScopeAdmin</c> who owns it (FR-AP-03).
    /// </summary>
    public const string OwnerNotValidForScope = "Owner is not a valid member or owner of the scope.";

    /// <summary>AF-16d: the application name was not supplied.</summary>
    public const string NameRequired = "Application name is required.";

    /// <summary>AF-16d: the application name exceeds the maximum length.</summary>
    public const string NameTooLong = "Application name must be at most 200 characters.";

    /// <summary>AF-16d: no owner was supplied.</summary>
    public const string OwnerRequired = "Owner is required.";
}
