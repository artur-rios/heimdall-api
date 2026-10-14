using ArturRios.Heimdall.Domain.Entities;

namespace ArturRios.Heimdall.Domain.Persistence;

/// <summary>
///     Adds a credential only while its application holds fewer than the allowed number of live
///     ones (UC-53 AF-53d, FR-AP-11), as one step that concurrent requests cannot both pass.
/// </summary>
public interface IApplicationCredentialSlots
{
    /// <param name="credential">The new credential, its <c>ApplicationId</c> set.</param>
    /// <param name="maxLive">How many live credentials the application may hold.</param>
    /// <param name="now">The instant liveness is judged at.</param>
    /// <returns><see langword="true" /> when the credential was stored; <see langword="false" /> when the limit was already reached.</returns>
    Task<bool> TryAddWithinLimitAsync(ApplicationCredential credential, int maxLive, DateTime now);
}
