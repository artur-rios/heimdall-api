using ArturRios.Data.Relational.Core.Entities;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Permanently removes the rows that hold data belonging to persons whose identity is being
///     anonymised: their password reset and email verification tokens, and their two-factor
///     configuration — whose <c>ON DELETE CASCADE</c> foreign keys take the recovery codes and email
///     codes with it.
/// </summary>
/// <remarks>
///     Shared by the scheduled anonymisation pass (NFR-20) and the re-application of erasures after
///     a restore (NFR-25), so the two cannot disagree about what an erased person leaves behind. Only
///     the first used to remove these rows; the second anonymised the person and kept the encrypted
///     TOTP secret, and the scheduled pass never came back for it, since it selects only records not
///     yet anonymised.
/// </remarks>
public static class PersonDependentsRemoval
{
    /// <returns>How many rows the deletes reported removing, and any errors they returned.</returns>
    public static async Task<(int Removed, IReadOnlyList<string> Errors)> RemoveAsync(
        IReadOnlyCollection<long> personIds,
        IAsyncRepository<PasswordResetToken, long> passwordResetTokens,
        IAsyncRepository<EmailVerificationToken, long> emailVerificationTokens,
        IAsyncRepository<TwoFactorAuth, long> twoFactorAuths)
    {
        if (personIds.Count == 0)
        {
            return (0, []);
        }

        var removed = 0;
        var errors = new List<string>();

        var passwordResetTokenIds = await passwordResetTokens.Query()
            .Where(token => personIds.Contains(token.PersonId))
            .Select(token => token.Id)
            .ToListAsync();

        var emailVerificationTokenIds = await emailVerificationTokens.Query()
            .Where(token => personIds.Contains(token.PersonId))
            .Select(token => token.Id)
            .ToListAsync();

        var twoFactorAuthIds = await twoFactorAuths.Query()
            .Where(configuration => personIds.Contains(configuration.PersonId))
            .Select(configuration => configuration.Id)
            .ToListAsync();

        Collect(await DeleteAllAsync(passwordResetTokenIds, passwordResetTokens));
        Collect(await DeleteAllAsync(emailVerificationTokenIds, emailVerificationTokens));
        Collect(await DeleteAllAsync(twoFactorAuthIds, twoFactorAuths));

        return (removed, errors);

        void Collect((int Removed, IEnumerable<string> Errors) result)
        {
            removed += result.Removed;
            errors.AddRange(result.Errors);
        }
    }

    /// <summary>
    ///     Permanently removes the entities with the given ids, or does nothing when there are none.
    ///     Reports how many rows were actually removed, which is what the delete reported rather
    ///     than what was selected.
    /// </summary>
    private static async Task<(int Removed, IEnumerable<string> Errors)> DeleteAllAsync<T>(
        IReadOnlyCollection<long> ids, IAsyncRepository<T, long> writer) where T : Entity<long>
    {
        if (ids.Count == 0)
        {
            return (0, []);
        }

        var deletion = await writer.DeleteRangeAsync(ids);

        return deletion.Success
            ? (deletion.Data?.Count() ?? 0, [])
            : (0, deletion.Errors);
    }
}
