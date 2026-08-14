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
///         The App and Email checks both run, in that order, and a request that fails either one
///         activates nothing — the pending row stays pending and can be confirmed by a later attempt.
///         One thing does change on the way through, though: an app code that verified is spent, so
///         a request whose App check passed and whose Email check then failed has to be retried with
///         a <em>fresh</em> app code. That is the single-use rule working as intended
///         (<see cref="ITotpCodeVerifier" />), not an accident of ordering — a code presented to this
///         endpoint has been presented, whatever the rest of the request went on to do.
///     </para>
///     <para>
///         <b>Write order.</b> The recovery codes are persisted <em>before</em>
///         <see cref="TwoFactorAuth.IsActive" /> is set, and in a single
///         <see cref="IAsyncRepository{T}.CreateRangeAsync" /> rather than ten separate inserts. The
///         repository layer exposes no transaction, so ordering is what stands in for one: the
///         dangerous half-write is "two-factor is active but the caller holds no recovery codes",
///         which would lock a caller out of their own account on the strength of a request this
///         handler reported as failed. Writing the codes first makes the surviving failure modes
///         harmless — codes stored against a configuration that never activated, which the next
///         attempt replaces, and which authenticate nothing on their own while
///         <see cref="TwoFactorAuth.IsActive" /> is <c>false</c>.
///     </para>
/// </remarks>
public class ConfirmTwoFactorAuthCommandHandler(
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncReadOnlyRepository<TwoFactorAuth> twoFactorReader,
    IAsyncRepository<TwoFactorAuth> twoFactorWriter,
    IAsyncReadOnlyRepository<TwoFactorEmailCode> emailCodeReader,
    IAsyncRepository<TwoFactorEmailCode> emailCodeWriter,
    IAsyncReadOnlyRepository<TwoFactorRecoveryCode> recoveryCodeReader,
    IAsyncRepository<TwoFactorRecoveryCode> recoveryCodeWriter,
    ITotpCodeVerifier totpCodeVerifier)
    : ICommandHandlerAsync<ConfirmTwoFactorAuthCommand, ConfirmTwoFactorAuthCommandOutput>
{
    private const int RecoveryCodeCount = 10;
    private const int RecoveryCodeSegmentLength = 4;

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

        // AF-37b: the App method's code, if the method was selected. ITotpCodeVerifier owns the
        // secret, the clock-drift window, and the single-use rule, shared with UC-38/39/40's
        // ITwoFactorFactorVerifier.
        if (twoFactorAuth.AppEnabled && !await totpCodeVerifier.VerifyAsync(twoFactorAuth, command.AppCode))
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

        // UC-37 steps 4-5 (FR-2F-05): ten fresh recovery codes, hashed at rest, returned once —
        // written before the activation below, per the write-order note in the remarks.
        var recoveryCodes = GenerateRecoveryCodes();

        var issuance = await IssueRecoveryCodesAsync(twoFactorAuth.Id, recoveryCodes);

        if (issuance is not null)
        {
            return output.WithErrors(issuance);
        }

        // UC-37 step 3: every required check passed and the caller now holds their recovery codes,
        // so the configuration can be activated.
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

        return output
            .WithData(new ConfirmTwoFactorAuthCommandOutput { Enabled = true, RecoveryCodes = recoveryCodes })
            .WithMessage(TwoFactorMessages.SetupConfirmed);
    }

    /// <summary>
    ///     Finds the latest not-yet-used, not-yet-expired email code for this configuration that
    ///     <paramref name="emailCode" /> hashes to, the same comparison
    ///     <see cref="EnableTwoFactorAuthCommandHandler" /> stores it with (AF-37c). Returns
    ///     <c>null</c> for a missing code, an incorrect one, or one that has expired or was already
    ///     used — UC-37 does not distinguish between them.
    /// </summary>
    private Task<TwoFactorEmailCode?> FindMatchingEmailCodeAsync(long twoFactorAuthId, string? emailCode) =>
        TwoFactorEmailCodeVerification.FindMatchingAsync(
            emailCodeReader, emailCodeWriter, twoFactorAuthId, emailCode);

    /// <summary>
    ///     Persists <paramref name="recoveryCodes" /> as the configuration's whole recovery code set,
    ///     replacing anything already stored against it, in one insert. Returns the persistence errors,
    ///     or <c>null</c> on success.
    /// </summary>
    /// <remarks>
    ///     The delete is what makes a retry after a failed attempt safe: a prior run that stored its
    ///     codes and then failed to activate left ten rows nobody was ever told about, and without
    ///     this the next attempt would leave them in place alongside the ten it does return — the
    ///     same "old codes valid alongside new ones" UC-40 exists to prevent.
    /// </remarks>
    private async Task<IEnumerable<string>?> IssueRecoveryCodesAsync(
        long twoFactorAuthId, IReadOnlyCollection<string> recoveryCodes)
    {
        var supersededIds = await recoveryCodeReader.Query()
            .Where(x => x.TwoFactorAuthId == twoFactorAuthId)
            .Select(x => x.Id)
            .ToListAsync();

        if (supersededIds.Count > 0)
        {
            var deletion = await recoveryCodeWriter.DeleteRangeAsync(supersededIds);

            if (!deletion.Success)
            {
                return deletion.Errors;
            }
        }

        var creation = await recoveryCodeWriter.CreateRangeAsync(recoveryCodes.Select(recoveryCode =>
            new TwoFactorRecoveryCode
            {
                TwoFactorAuthId = twoFactorAuthId, CodeHash = HashRecoveryCode(recoveryCode), Used = false
            }));

        return creation.Success ? null : creation.Errors;
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
