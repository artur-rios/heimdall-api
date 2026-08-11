using System.Security.Cryptography;
using System.Text;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Util.Hashing;
using Microsoft.EntityFrameworkCore;
using OtpNet;

namespace ArturRios.Heimdall.Command.Services;

/// <inheritdoc cref="ITwoFactorFactorVerifier" />
public class TwoFactorFactorVerifier(
    IAsyncReadOnlyRepository<TwoFactorEmailCode> emailCodeReader,
    IAsyncReadOnlyRepository<TwoFactorRecoveryCode> recoveryCodeReader,
    ITotpSecretProtector totpSecretProtector) : ITwoFactorFactorVerifier
{
    // Same one time-step (30s) tolerance ConfirmTwoFactorAuthCommandHandler allows for clock drift.
    private static readonly VerificationWindow TotpVerificationWindow = new(previous: 1, future: 1);

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

        if (twoFactorAuth.AppEnabled && VerifyAppCode(twoFactorAuth, code))
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

    /// <summary>Decrypts the stored TOTP secret and checks <paramref name="code" /> against it (FR-2F-09).</summary>
    private bool VerifyAppCode(TwoFactorAuth twoFactorAuth, string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || twoFactorAuth.TotpSecretEncrypted is null)
        {
            return false;
        }

        string base32Secret;

        try
        {
            base32Secret = totpSecretProtector.Unprotect(twoFactorAuth.TotpSecretEncrypted);
        }
        catch (CryptographicException)
        {
            // A corrupted or otherwise unreadable TotpSecretEncrypted value (e.g. protected under a
            // Data Protection key that is no longer available) can never be decrypted back into a
            // valid app code, so it fails the same way a wrong code does instead of surfacing as a
            // 500.
            return false;
        }

        var totp = new Totp(Base32Encoding.ToBytes(base32Secret));

        return totp.VerifyTotp(code, out _, TotpVerificationWindow);
    }

    /// <summary>
    ///     Finds a not-yet-used, not-yet-expired email code for this configuration that
    ///     <paramref name="code" /> hashes to — the same comparison
    ///     <c>ConfirmTwoFactorAuthCommandHandler</c> uses. Returns <c>null</c> for a missing,
    ///     incorrect, expired, or already-used code, all alike.
    /// </summary>
    private async Task<TwoFactorEmailCode?> FindMatchingEmailCodeAsync(long twoFactorAuthId, string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var now = DateTime.UtcNow;

        var live = await emailCodeReader.Query()
            .Where(x => x.TwoFactorAuthId == twoFactorAuthId && !x.Used && x.ExpiresAt > now)
            .ToListAsync();

        return live.FirstOrDefault(x => Hash.TextMatches(code, x.CodeHash, x.Salt));
    }

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
