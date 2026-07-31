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

    /// <summary>AF-16e: a Scope Admin acted on a scope they do not own.</summary>
    public const string NotScopeOwner = "You are not an owner of the target scope.";

    /// <summary>AF-16c: a Scope Admin named someone other than themself as the owner.</summary>
    public const string CannotSetAnotherOwner = "You may only create applications you own.";

    /// <summary>
    ///     AF-16b: the owner does not exist, is logically deleted, does not carry the
    ///     <c>ScopeAdmin</c> role, or does not own the target scope (FR-AP-03).
    /// </summary>
    public const string OwnerNotValidForScope = "Owner must be a Scope Admin who owns the target scope.";

    /// <summary>AF-16d: the application name was not supplied.</summary>
    public const string NameRequired = "Application name is required.";

    /// <summary>AF-16d: the application name exceeds the maximum length.</summary>
    public const string NameTooLong = "Application name must be at most 200 characters.";

    /// <summary>AF-16d: no owner was supplied.</summary>
    public const string OwnerRequired = "Owner is required.";

    /// <summary>UC-17 success: a single application was retrieved.</summary>
    public const string ApplicationRetrievedSuccessfully = "Application retrieved successfully.";

    /// <summary>UC-17 success: a list of applications was retrieved.</summary>
    public const string ApplicationsRetrievedSuccessfully = "Applications retrieved successfully.";

    /// <summary>
    ///     AF-17a: no application holds that identifier inside the addressed scope — or it is
    ///     logically deleted and was not explicitly requested (FR-AP-09).
    /// </summary>
    public const string ApplicationNotFound = "Application not found.";

    /// <summary>AF-17b: the caller is not allowed to view the requested application.</summary>
    public const string NotAuthorizedToViewApplication = "You are not allowed to view this application.";

    /// <summary>UC-18 success: the application was updated.</summary>
    public const string ApplicationUpdatedSuccessfully = "Application updated successfully.";

    /// <summary>AF-18c: the caller is not allowed to update the requested application.</summary>
    public const string NotAuthorizedToUpdateApplication = "You are not allowed to update this application.";
}
