using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     Google User data returned by the UC-27 view/list queries. Carries FR-GO-05's registered fields
///     plus the timestamps every other output exposes, with the internal <c>Id</c> left out
///     (NFR-15). There is no password field to omit — a Google User has none, because authentication
///     is delegated to Google.
/// </summary>
public class GoogleUserOutput : QueryOutput
{
    /// <summary>Public identifier of the Google User.</summary>
    public Guid Id { get; set; }

    /// <summary>
    ///     Google's stable <c>sub</c> claim. Exposed deliberately: it is the identifier FR-GO-08 makes
    ///     unique within the scope, and an administrator correlating the record with a Google account
    ///     has nothing else to correlate on. It is not secret — a caller who may read this record
    ///     already knows who it belongs to.
    /// </summary>
    public string GoogleId { get; set; } = string.Empty;

    /// <summary>Full name, as it stood on the Google ID token the account last signed up with.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Email address.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Whether Google reported the address as verified.</summary>
    public bool EmailVerified { get; set; }

    /// <summary>Profile picture URL, or <c>null</c> when the token carried no <c>picture</c> claim.</summary>
    public string? ProfilePictureUrl { get; set; }

    /// <summary>Whether the Google User is logically deleted.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Public identifier of the one scope the Google User belongs to (FR-GO-06).</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last update timestamp.</summary>
    public DateTime UpdatedAt { get; set; }
}
