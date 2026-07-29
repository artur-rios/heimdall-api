namespace ArturRios.IdentityManager.Shared.Messages;

/// <summary>
///     Canonical messages produced by the person use cases. Each is mapped to an HTTP status code in
///     <see cref="PersonMessageMap" />.
/// </summary>
public static class PersonMessages
{
    /// <summary>UC-06 success: the person was created.</summary>
    public const string PersonCreatedSuccessfully = "Person created successfully.";

    /// <summary>AF-06a: the email is already in use (within the scope for Users, system-wide for admins).</summary>
    public const string EmailAlreadyExists = "A person with this email already exists.";

    /// <summary>AF-06b: the target scope does not exist or is logically deleted.</summary>
    public const string ScopeNotFound = "Scope not found.";

    /// <summary>AF-06e: a Scope Admin acted on a scope they do not own.</summary>
    public const string NotScopeOwner = "You are not an owner of the target scope.";

    /// <summary>AF-06d: name was not supplied.</summary>
    public const string NameRequired = "Name is required.";

    /// <summary>AF-06d: name exceeds the maximum length.</summary>
    public const string NameTooLong = "Name must be at most 200 characters.";

    /// <summary>AF-06d: email was not supplied.</summary>
    public const string EmailRequired = "Email is required.";

    /// <summary>AF-06d: email is not a valid address.</summary>
    public const string EmailInvalid = "Email is not valid.";

    /// <summary>AF-06d: password was not supplied.</summary>
    public const string PasswordRequired = "Password is required.";

    /// <summary>AF-06d: password is shorter than the minimum length.</summary>
    public const string PasswordTooShort = "Password must be at least 8 characters.";

    /// <summary>AF-06d: the requested role is not ScopeAdmin or SystemAdmin (path b).</summary>
    public const string InvalidRole = "Role must be ScopeAdmin or SystemAdmin.";

    /// <summary>UC-07 success: a single person was retrieved.</summary>
    public const string PersonRetrievedSuccessfully = "Person retrieved successfully.";

    /// <summary>UC-07 success: a list of persons was retrieved.</summary>
    public const string PersonsRetrievedSuccessfully = "Persons retrieved successfully.";

    /// <summary>AF-07a: the requested person does not exist (or is logically deleted and not requested).</summary>
    public const string PersonNotFound = "Person not found.";

    /// <summary>AF-07b: the caller is not allowed to view the requested person.</summary>
    public const string NotAuthorizedToViewPerson = "You are not allowed to view this person.";
}
