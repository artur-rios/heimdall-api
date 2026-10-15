namespace ArturRios.Heimdall.Shared.Security;

/// <summary>
///     The claims an application token carries (UC-56, FR-AP-13). Consumers read them after
///     validating the token offline; the consumer guide lists them.
/// </summary>
public static class ApplicationTokenClaims
{
    /// <summary>
    ///     Marks the token's kind. Present with <see cref="ApplicationKind" /> on every application
    ///     token and on no person token, so <c>IdentityUserMapper</c> refuses any token carrying it.
    /// </summary>
    public const string TokenKind = "tokenKind";

    /// <summary>The value of <see cref="TokenKind" /> on an application token.</summary>
    public const string ApplicationKind = "application";

    /// <summary>The application's <c>PublicId</c>.</summary>
    public const string Id = "id";

    /// <summary>The <c>PublicId</c> of the application's scope.</summary>
    public const string ScopeId = "scopeId";

    /// <summary>The <c>PublicId</c> of the credential the token was issued from.</summary>
    public const string CredentialId = "credentialId";

    /// <summary>The application's name when the token was issued.</summary>
    public const string Name = "name";
}
