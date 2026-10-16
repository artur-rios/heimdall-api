using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Retires a person's outstanding password-reset links when their address changes (UC-08,
///     FR-PE-17): a reset link was mailed to the address being replaced, and it would otherwise let
///     whoever reads that mailbox set the password for the rest of its lifetime. The counterpart of
///     <see cref="EmailVerificationTokenRetirement" />, and it leaves the same things alone.
/// </summary>
/// <remarks>
///     Already-expired tokens are left alone — AF-13a refuses them either way, and rewriting them
///     would only make a dead token report a different reason for being dead — and another person's
///     tokens are never touched.
/// </remarks>
public static class PasswordResetTokenRetirement
{
    /// <summary>
    ///     Marks every live (unused, unexpired at <paramref name="now" />) reset token of the person
    ///     with internal id <paramref name="personId" /> as used.
    /// </summary>
    /// <returns>The update errors of the first failed write, or <c>null</c> when all succeeded.</returns>
    public static async Task<IEnumerable<string>?> RetireLiveAsync(
        IAsyncReadOnlyRepository<PasswordResetToken, long> tokenReader,
        IAsyncRepository<PasswordResetToken, long> tokenWriter,
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
