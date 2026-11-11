using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>UC-57's answer: the application the token stands for, as it is now.</summary>
public class ApplicationTokenCheckOutput : QueryOutput
{
    public Guid ApplicationId { get; init; }

    /// <summary>The application's current name, which may differ from the token's <c>name</c> claim after a rename.</summary>
    public string Name { get; init; } = string.Empty;

    public Guid ScopeId { get; init; }

    public Guid CredentialId { get; init; }

    /// <summary>When the presented token expires.</summary>
    public DateTime ExpiresAt { get; init; }
}
