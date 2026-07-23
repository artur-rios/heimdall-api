namespace ArturRios.IdentityManager.Command.Messages;

/// <summary>
///     Canonical messages produced by the scope commands. Every message that reaches a caller is
///     declared here so it can be validated against and mapped to an HTTP status code in
///     <see cref="ScopeMessageMap" />.
/// </summary>
public static class ScopeMessages
{
    /// <summary>UC-01 success: the scope was created.</summary>
    public const string ScopeCreatedSuccessfully = "Scope created successfully.";

    /// <summary>AF-01b: the scope name was not supplied.</summary>
    public const string NameRequired = "Scope name is required.";

    /// <summary>AF-01b: no owner was supplied.</summary>
    public const string AtLeastOneOwnerRequired = "At least one owner must be specified.";

    /// <summary>AF-01a: a scope with the requested name already exists.</summary>
    public const string NameAlreadyExists = "A scope with this name already exists.";

    /// <summary>AF-01d: an owner does not reference an existing, non-deleted ScopeAdmin.</summary>
    public const string OwnerNotValidScopeAdmin =
        "One or more owners do not reference an existing, non-deleted ScopeAdmin.";

    /// <summary>The ScopeAdmin role reference data is missing — a server configuration problem.</summary>
    public const string ScopeAdminRoleNotConfigured = "The ScopeAdmin role is not configured.";
}
