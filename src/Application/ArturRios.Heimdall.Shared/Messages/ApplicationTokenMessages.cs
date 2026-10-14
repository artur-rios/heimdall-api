namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>Messages of UC-56 (obtain an application token) and UC-57 (check one live).</summary>
public static class ApplicationTokenMessages
{
    /// <summary>UC-56 main flow.</summary>
    public const string TokenIssued = "Application token issued.";

    /// <summary>UC-57 main flow.</summary>
    public const string TokenAccepted = "Application token accepted.";

    /// <summary>AF-56a — the body is not JSON, or an id in it is not a GUID, so no command could be read.</summary>
    public const string RequestMalformed = "The request body is malformed.";

    /// <summary>AF-56a — the application id is missing.</summary>
    public const string ApplicationIdRequired = "Application id is required.";

    /// <summary>AF-56a — the scope id is missing.</summary>
    public const string ScopeIdRequired = "Scope id is required.";

    /// <summary>AF-56a — the secret is missing.</summary>
    public const string SecretRequired = "Secret is required.";

    /// <summary>AF-56a — the secret is over-long.</summary>
    public const string SecretTooLong = "Secret must be at most 100 characters.";

    /// <summary>AF-56b — every refusal, whatever failed: one message so the endpoint reveals nothing.</summary>
    public const string CredentialsNotAccepted = "The application credentials were not accepted.";

    /// <summary>AF-57a — every refusal of the re-check, for the same reason.</summary>
    public const string TokenNotAccepted = "The application token was not accepted.";
}
