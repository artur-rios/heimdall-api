using System.Security.Cryptography;
using System.Text;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Util.Hashing;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Services;

/// <inheritdoc cref="ITwoFactorFactorVerifier" />
public class TwoFactorFactorVerifier(
    IAsyncReadOnlyRepository<TwoFactorEmailCode> emailCodeReader,
    IAsyncRepository<TwoFactorEmailCode> emailCodeWriter,
    IAsyncReadOnlyRepository<TwoFactorRecoveryCode> recoveryCodeReader,
    ITotpCodeVerifier totpCodeVerifier) : ITwoFactorFactorVerifier
{
    public async Task<TwoFactorFactorVerificationResult> VerifyAsync(
        TwoFactorAuth twoFactorAuth, string? code, string? recoveryCode)
    {
        if (!string.IsNullOrWhiteSpace(recoveryCode))
        {
            var matchingRecoveryCode = await FindMatchingRecoveryCodeAsync(twoFactorAuth.Id, recoveryCode);

            return matchingRecoveryCode is null
                ? TwoFactorFactorVerificationResult.NoMatch
                : TwoFactorFactorVerificationResult.ForRecoveryCode(matchingRecoveryCode);
        }

        // ITotpCodeVerifier owns the secret, the window, and the single-use rule; this class only
        // decides which factor to try.
        if (twoFactorAuth.AppEnabled && await totpCodeVerifier.VerifyAsync(twoFactorAuth, code))
        {
            return TwoFactorFactorVerificationResult.AppCodeMatch;
        }

        if (twoFactorAuth.EmailEnabled)
        {
            var matchingEmailCode = await FindMatchingEmailCodeAsync(twoFactorAuth.Id, code);

            if (matchingEmailCode is not null)
            {
                return TwoFactorFactorVerificationResult.ForEmailCode(matchingEmailCode);
            }
        }

        return TwoFactorFactorVerificationResult.NoMatch;
    }

    /// <summary>
    ///     Finds a not-yet-used, not-yet-expired email code for this configuration that
    ///     <paramref name="code" /> hashes to — the same comparison
    ///     <c>ConfirmTwoFactorAuthCommandHandler</c> uses, through the shared
    ///     <see cref="TwoFactorEmailCodeVerification" />. Returns <c>null</c> for a missing,
    ///     incorrect, expired, already-used, or exhausted code, all alike.
    /// </summary>
    private Task<TwoFactorEmailCode?> FindMatchingEmailCodeAsync(long twoFactorAuthId, string? code) =>
        TwoFactorEmailCodeVerification.FindMatchingAsync(
            emailCodeReader, emailCodeWriter, twoFactorAuthId, code);

    /// <summary>
    ///     Finds an unused recovery code for this configuration whose hash matches
    ///     <paramref name="recoveryCode" />. An unknown code and an already-used one both return
    ///     <c>null</c> here — the query itself excludes used rows, so the two cases are
    ///     indistinguishable by construction.
    /// </summary>
    private async Task<TwoFactorRecoveryCode?> FindMatchingRecoveryCodeAsync(
        long twoFactorAuthId, string recoveryCode)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(recoveryCode));

        var unused = await recoveryCodeReader.Query()
            .Where(x => x.TwoFactorAuthId == twoFactorAuthId && !x.Used)
            .ToListAsync();

        return unused.FirstOrDefault(x => x.CodeHash.SequenceEqual(hash));
    }
}
