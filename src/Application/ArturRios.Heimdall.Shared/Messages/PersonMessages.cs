namespace ArturRios.Heimdall.Shared.Messages;

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

    /// <summary>UC-08 success: the person was updated.</summary>
    public const string PersonUpdatedSuccessfully = "Person updated successfully.";

    /// <summary>UC-08: the caller is not allowed to update the requested person.</summary>
    public const string NotAuthorizedToUpdatePerson = "You are not allowed to update this person.";

    /// <summary>AF-08c: only a System Admin may change a person's role.</summary>
    public const string RoleChangeRequiresSystemAdmin = "Only a System Admin may change a person's role.";

    /// <summary>
    ///     UC-08: the requested role change would need a target scope the request does not carry.
    ///     Only a change to SystemAdmin is supported here.
    /// </summary>
    public const string UnsupportedRoleTransition =
        "Only a change to SystemAdmin is supported here. To make a person a scope owner, use the " +
        "scope owner endpoints.";

    /// <summary>NFR-12: the change would leave a scope without any owner.</summary>
    public const string ScopeWouldLoseLastOwner =
        "This change would leave a scope without an owner. Add another owner first.";

    /// <summary>UC-08: the supplied role is not one of the three defined roles.</summary>
    public const string UnknownRole = "Role must be SystemAdmin, ScopeAdmin, or User.";

    /// <summary>UC-09 success: the person was logically deleted, or already was (AF-09b).</summary>
    public const string PersonDeletedSuccessfully = "Person deleted successfully.";

    /// <summary>AF-09c: the caller is not allowed to delete the requested person.</summary>
    public const string NotAuthorizedToDeletePerson = "You are not allowed to delete this person.";

    /// <summary>AF-09d / AF-10c: an actor may not delete their own person record.</summary>
    public const string CannotDeleteSelf = "You cannot delete your own person record.";

    /// <summary>UC-10 success: the person was permanently (hard) deleted.</summary>
    public const string PersonHardDeletedSuccessfully = "Person hard deleted successfully.";

    /// <summary>UC-21 success: the person was added as an owner of the scope.</summary>
    public const string ScopeOwnerAddedSuccessfully = "Scope owner added successfully.";

    /// <summary>AF-21d: the person already owns the scope, so nothing was written.</summary>
    public const string AlreadyScopeOwner = "Person is already an owner of this scope.";

    /// <summary>
    ///     AF-21b: the person named as the new owner does not exist, is logically deleted, or does
    ///     not hold the <c>ScopeAdmin</c> role. All three answer alike so the endpoint cannot be used
    ///     to probe which persons exist.
    /// </summary>
    public const string PersonNotValidScopeAdmin = "The person must be an existing, non-deleted ScopeAdmin.";

    /// <summary>UC-22 success: the person's ownership of the scope was removed.</summary>
    public const string ScopeOwnerRemovedSuccessfully = "Scope owner removed successfully.";

    /// <summary>
    ///     AF-22a: the person named for removal does not exist, or holds no ownership row for the
    ///     target scope. Distinct from <see cref="NotScopeOwner" />, which is about the *caller* and
    ///     answers 403; this one is about the *target* and answers 404.
    /// </summary>
    public const string PersonNotScopeOwner = "The person is not an owner of this scope.";

    /// <summary>UC-23 success: the person was promoted from User to owner of the scope.</summary>
    public const string ScopeUserPromotedSuccessfully = "Person promoted to scope owner successfully.";

    /// <summary>
    ///     AF-23b: the person named for promotion does not exist, is logically deleted, or is not a
    ///     <c>User</c> of the target scope. All the conditions answer alike so the endpoint cannot be
    ///     used to probe which persons exist or which scope they belong to.
    /// </summary>
    public const string PersonNotScopeUser = "The person must be an existing, non-deleted User of this scope.";

    /// <summary>AF-23d: the person already holds the <c>ScopeAdmin</c> role, so there is nothing to promote.</summary>
    public const string AlreadyScopeAdmin = "Person already holds the ScopeAdmin role.";
}
