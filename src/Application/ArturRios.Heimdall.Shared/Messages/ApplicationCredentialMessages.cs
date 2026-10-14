namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>Messages of UC-53 (issue), UC-54 (list) and UC-55 (revoke) — an application's credentials.</summary>
public static class ApplicationCredentialMessages
{
    /// <summary>UC-53 main flow.</summary>
    public const string CredentialIssued =
        "Application credential issued. Store the secret now: it is never shown again.";

    /// <summary>UC-54 main flow.</summary>
    public const string CredentialsRetrieved = "Application credentials retrieved.";

    /// <summary>UC-55 main flow.</summary>
    public const string CredentialRevoked = "Application credential revoked.";

    /// <summary>AF-53c — the requested expiry is not in the future.</summary>
    public const string ExpiryNotInFuture = "The expiry must be in the future.";

    /// <summary>
    ///     AF-53c — the body is not JSON, or its <c>expiresAt</c> is not a date-time, so no expiry
    ///     could be read. Refused rather than issued without one: a credential meant to expire must
    ///     never silently become one that does not.
    /// </summary>
    public const string RequestMalformed = "The request body is malformed.";

    /// <summary>AF-53b, AF-54b, AF-55b — the actor may not manage this application.</summary>
    public const string NotAuthorizedToManageCredentials =
        "You are not allowed to manage this application's credentials.";

    /// <summary>AF-55c — no such credential on this application.</summary>
    public const string CredentialNotFound = "Application credential not found.";

    /// <summary>AF-53d — two live credentials already exist.</summary>
    public const string CredentialLimitReached =
        "This application already has two live credentials. Revoke one before issuing another.";

    /// <summary>AF-55d — the credential was already revoked.</summary>
    public const string CredentialAlreadyRevoked = "This application credential is already revoked.";
}
