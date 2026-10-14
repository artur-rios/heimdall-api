namespace ArturRios.Heimdall.Command.Services;

/// <summary>Who an application token is issued to (UC-56).</summary>
/// <param name="ApplicationId">The application's <c>PublicId</c>.</param>
/// <param name="ScopeId">Its scope's <c>PublicId</c>.</param>
/// <param name="CredentialId">The <c>PublicId</c> of the credential that was presented.</param>
/// <param name="Name">The application's name.</param>
public sealed record ApplicationTokenSubject(Guid ApplicationId, Guid ScopeId, Guid CredentialId, string Name);

/// <summary>Issues UC-56's application token (FR-AP-13).</summary>
public interface IApplicationTokenIssuer
{
    /// <returns>The signed token and when it expires.</returns>
    Task<AuthToken> IssueAsync(ApplicationTokenSubject subject);
}
