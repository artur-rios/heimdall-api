using System.Security.Cryptography;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using OtpNet;

namespace ArturRios.Heimdall.Command.Services;

/// <inheritdoc cref="ITotpCodeVerifier" />
public class TotpCodeVerifier(
    ITotpSecretProtector totpSecretProtector,
    IAtomicWrites atomicWrites) : ITotpCodeVerifier
{
    // A one time-step (30s) tolerance on either side of "now", the conventional allowance for clock
    // drift between the server and whatever device generated the code — wide enough to forgive a
    // little skew, narrow enough that it does not meaningfully widen the guessing window.
    private static readonly VerificationWindow TotpVerificationWindow = new(previous: 1, future: 1);

    public async Task<bool> VerifyAsync(TwoFactorAuth twoFactorAuth, string? code)
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

        if (!totp.VerifyTotp(code, out var matchedTimeStep, TotpVerificationWindow))
        {
            return false;
        }

        // FR-2F-14 / RFC 6238 §5.2: a code is good once. The window above accepts the neighbouring
        // steps too, so without this an observed code stays usable for up to ninety seconds and
        // across every endpoint that takes a second factor — verify, disable, and regenerate alike.
        // Strictly-greater rather than not-equal, so a replay of an older step inside the window is
        // refused as well.
        //
        // The comparison is made by the database, in the write that records the step: two requests
        // carrying the same code at the same moment both pass a check made in memory, and both used
        // to be accepted. Only one of them can advance the step, and only that one is accepted. A
        // failure to record it is a failure to verify for the same reason — accepting a code whose
        // use was not recorded would leave it replayable.
        return await atomicWrites.TryAdvanceTotpStepAsync(twoFactorAuth, matchedTimeStep, DateTime.UtcNow);
    }
}
