using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Retires a person's outstanding email-verification links (UC-15 step 3, UC-51), so that once a
///     new link is mailed it is the only one that works. Shared by both use cases so the rule cannot
///     drift between them.
/// </summary>
/// <remarks>
///     Already-expired tokens are left alone — AF-14a refuses them either way, and rewriting them
///     would only make a dead token report a different reason for being dead — and another person's
///     tokens are never touched.
/// </remarks>
public static class EmailVerificationTokenRetirement
{
    /// <summary>
    ///     Marks every live (unused, unexpired at <paramref name="now" />) verification token of the
    ///     person with internal id <paramref name="personId" /> as used.
    /// </summary>
    /// <returns>The update errors of the first failed write, or <c>null</c> when all succeeded.</returns>
    public static async Task<IEnumerable<string>?> RetireLiveAsync(
        IAsyncReadOnlyRepository<EmailVerificationToken, long> tokenReader,
        IAsyncRepository<EmailVerificationToken, long> tokenWriter,
        long personId,
        DateTime now)
    {
        var live = await tokenReader.Query()
            .Where(x => x.PersonId == personId && !x.Used && x.ExpiresAt > now)
            .ToListAsync();

        foreach (var outstanding in live)
        {
            outstanding.Used = true;

            var update = await tokenWriter.UpdateAsync(outstanding);

            if (!update.Success)
            {
                return update.Errors;
            }
        }

        return null;
    }
}
