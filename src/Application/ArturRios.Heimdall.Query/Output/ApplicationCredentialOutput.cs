namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     One credential in UC-54's listing. Never the secret, which is shown once by UC-53, and never
///     its hash.
/// </summary>
public class ApplicationCredentialOutput
{
    /// <summary>The credential's <c>PublicId</c> — UC-55's route value and the token's <c>credentialId</c>.</summary>
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    /// <summary>When a token was last issued from it (UC-56), or <c>null</c> if never.</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>Whether UC-56 would accept it now: not revoked and not expired.</summary>
    public bool Live { get; set; }
}
