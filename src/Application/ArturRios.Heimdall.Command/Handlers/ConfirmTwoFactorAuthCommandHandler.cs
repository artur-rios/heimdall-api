using System.Security.Cryptography;
using System.Text;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Random;
using Microsoft.EntityFrameworkCore;
using OtpNet;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="ConfirmTwoFactorAuthCommand" /> (UC-37, FR-2F-04/05): loads the caller's
///     pending <see cref="TwoFactorAuth" /> row (AF-37a), refuses a caller who has already confirmed
///     (AF-37d), verifies a currently valid code for every method the row has enabled (AF-37b,
///     AF-37c), then — only once every required check passes — activates the configuration and
///     issues ten recovery codes, returned in plaintext exactly once.
/// </summary>
/// <remarks>
///     <para>
///         There is no shape validator, unlike <see cref="EnableTwoFactorAuthCommandHandler" />'s:
///         which code(s) are required depends on <see cref="TwoFactorAuth.AppEnabled" />/
///         <see cref="TwoFactorAuth.EmailEnabled" />, a database read no validator has business
///         making. A caller who names no live person at all — a Google User, or a bearer token
///         naming a person since hard deleted — is indistinguishable from AF-37a here: neither could
///         ever hold a <c>TwoFactorAuth</c> row to confirm, so both answer the same 404 rather than
///         inventing a second refusal UC-37 does not define.
///     </para>
///     <para>
///         The App and Email checks both run, in that order, before anything is written — so a
///         request that fails either one leaves the pending row and its outstanding email code
///         completely untouched, and can be retried.
///     </para>
/// </remarks>
public class ConfirmTwoFactorAuthCommandHandler(
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncReadOnlyRepository<TwoFactorAuth> twoFactorReader,
    IAsyncRepository<TwoFactorAuth> twoFactorWriter,
    IAsyncReadOnlyRepository<TwoFactorEmailCode> emailCodeReader,
    IAsyncRepository<TwoFactorEmailCode> emailCodeWriter,
    IAsyncRepository<TwoFactorRecoveryCode> recoveryCodeWriter,
    ITotpSecretProtector totpSecretProtector)
    : ICommandHandlerAsync<ConfirmTwoFactorAuthCommand, ConfirmTwoFactorAuthCommandOutput>
{
    private const int RecoveryCodeCount = 10;
    private const int RecoveryCodeSegmentLength = 4;

    // A one time-step (30s) tolerance on either side of "now", the conventional allowance for clock
    // drift between the server and whatever device generated the code — wide enough to forgive a
    // little skew, narrow enough that it does not meaningfully widen the guessing window.
    private static readonly VerificationWindow TotpVerificationWindow = new(previous: 1, future: 1);

    public async Task<DataOutput<ConfirmTwoFactorAuthCommandOutput?>> HandleAsync(
        ConfirmTwoFactorAuthCommand command)
    {
        var output = DataOutput<ConfirmTwoFactorAuthCommandOutput?>.New;

        // AF-37a (and, implicitly, the Google User / hard-deleted-person case UC-36's AF-37b handles
        // separately for enable — here it collapses into "no pending setup", since neither could ever
        // hold a row to confirm).
        var person = await personReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ActingPersonId && !x.IsDeleted);

        var twoFactorAuth = person is null
            ? null
            : await twoFactorReader.Query().FirstOrDefaultAsync(x => x.PersonId == person.Id);

        if (twoFactorAuth is null)
        {
            return output.WithError(TwoFactorMessages.NoPendingSetup);
        }

        // AF-37d.
        if (twoFactorAuth.IsActive)
        {
            return output.WithError(TwoFactorMessages.AlreadyActive);
        }

        // AF-37b: the App method's code, if the method was selected.
        if (twoFactorAuth.AppEnabled && !VerifyAppCode(twoFactorAuth, command.AppCode))
        {
            return output.WithError(TwoFactorMessages.AppCodeInvalid);
        }

        // AF-37c: the Email method's code, if the method was selected.
        TwoFactorEmailCode? consumedEmailCode = null;

        if (twoFactorAuth.EmailEnabled)
        {
            consumedEmailCode = await FindMatchingEmailCodeAsync(twoFactorAuth.Id, command.EmailCode);

            if (consumedEmailCode is null)
            {
                return output.WithError(TwoFactorMessages.EmailCodeInvalid);
            }
        }

        // UC-37 step 3: every required check passed.
        twoFactorAuth.IsActive = true;

        var activation = await twoFactorWriter.UpdateAsync(twoFactorAuth);

        if (!activation.Success)
        {
            return output.WithErrors(activation.Errors);
        }

        // UC-37 step 7: the email code that confirmed setup can never be replayed.
        if (consumedEmailCode is not null)
        {
            consumedEmailCode.Used = true;

            var consumption = await emailCodeWriter.UpdateAsync(consumedEmailCode);

            if (!consumption.Success)
            {
                return output.WithErrors(consumption.Errors);
            }
        }

        // UC-37 steps 4-5 (FR-2F-05): ten fresh recovery codes, hashed at rest, returned once.
        var recoveryCodes = GenerateRecoveryCodes();

        foreach (var recoveryCode in recoveryCodes)
        {
            var creation = await recoveryCodeWriter.CreateAsync(new TwoFactorRecoveryCode
            {
                TwoFactorAuthId = twoFactorAuth.Id, CodeHash = HashRecoveryCode(recoveryCode), Used = false
            });

            if (!creation.Success)
            {
                return output.WithErrors(creation.Errors);
            }
        }

        return output
            .WithData(new ConfirmTwoFactorAuthCommandOutput { Enabled = true, RecoveryCodes = recoveryCodes })
            .WithMessage(TwoFactorMessages.SetupConfirmed);
    }

    /// <summary>
    ///     Decrypts the stored TOTP secret and checks <paramref name="appCode" /> against it, allowing
    ///     <see cref="TotpVerificationWindow" />'s tolerance for clock drift (FR-2F-04).
    /// </summary>
    private bool VerifyAppCode(TwoFactorAuth twoFactorAuth, string? appCode)
    {
        if (string.IsNullOrWhiteSpace(appCode) || twoFactorAuth.TotpSecretEncrypted is null)
        {
            return false;
        }

        var base32Secret = totpSecretProtector.Unprotect(twoFactorAuth.TotpSecretEncrypted);
        var totp = new Totp(Base32Encoding.ToBytes(base32Secret));

        return totp.VerifyTotp(appCode, out _, TotpVerificationWindow);
    }

    /// <summary>
    ///     Finds the latest not-yet-used, not-yet-expired email code for this configuration that
    ///     <paramref name="emailCode" /> hashes to, the same comparison
    ///     <see cref="EnableTwoFactorAuthCommandHandler" /> stores it with (AF-37c). Returns
    ///     <c>null</c> for a missing code, an incorrect one, or one that has expired or was already
    ///     used — UC-37 does not distinguish between them.
    /// </summary>
    private async Task<TwoFactorEmailCode?> FindMatchingEmailCodeAsync(long twoFactorAuthId, string? emailCode)
    {
        if (string.IsNullOrWhiteSpace(emailCode))
        {
            return null;
        }

        var now = DateTime.UtcNow;

        var live = await emailCodeReader.Query()
            .Where(x => x.TwoFactorAuthId == twoFactorAuthId && !x.Used && x.ExpiresAt > now)
            .ToListAsync();

        return live.FirstOrDefault(x => Hash.TextMatches(emailCode, x.CodeHash, x.Salt));
    }

    /// <summary>
    ///     Generates <see cref="RecoveryCodeCount" /> random, human-typeable recovery codes, formatted
    ///     as two <see cref="RecoveryCodeSegmentLength" />-character uppercase alphanumeric segments
    ///     separated by a hyphen (e.g. <c>"A1B2-C3D4"</c>), for FR-2F-05.
    /// </summary>
    private static List<string> GenerateRecoveryCodes()
    {
        var codes = new List<string>(RecoveryCodeCount);

        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var raw = CustomRandom.Text(new RandomStringOptions
            {
                Length = RecoveryCodeSegmentLength * 2,
                IncludeDigits = true,
                IncludeUppercase = true,
                IncludeLowercase = false,
                IncludeSpecialCharacters = false
            });

            codes.Add($"{raw[..RecoveryCodeSegmentLength]}-{raw[RecoveryCodeSegmentLength..]}");
        }

        return codes;
    }

    /// <summary>
    ///     Hashes a recovery code for storage. §4.10 of the System Requirements Document documents no
    ///     per-code salt column for <see cref="TwoFactorRecoveryCode" /> — unlike a user-chosen
    ///     password, a recovery code is already a high-entropy random string, so a plain SHA-256 digest
    ///     is a one-way function consistent with that schema without inventing a column the design
    ///     never called for.
    /// </summary>
    private static byte[] HashRecoveryCode(string recoveryCode) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(recoveryCode));
}
