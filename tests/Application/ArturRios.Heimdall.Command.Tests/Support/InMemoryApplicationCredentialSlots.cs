using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests.Support;

/// <summary>
///     <see cref="IApplicationCredentialSlots" /> over an <see cref="AsyncFakeRepository{TEntity,TKey}" />:
///     the same count-then-add, without the lock a single-threaded test does not need.
/// </summary>
public sealed class InMemoryApplicationCredentialSlots(AsyncFakeRepository<ApplicationCredential, long> credentials)
    : IApplicationCredentialSlots
{
    public async Task<bool> TryAddWithinLimitAsync(ApplicationCredential credential, int maxLive, DateTime now)
    {
        var live = credentials.Query()
            .Where(x => x.ApplicationId == credential.ApplicationId)
            .Count(ApplicationCredential.LiveAt(now));

        if (live >= maxLive)
        {
            return false;
        }

        await credentials.CreateAsync(credential);

        return true;
    }
}
