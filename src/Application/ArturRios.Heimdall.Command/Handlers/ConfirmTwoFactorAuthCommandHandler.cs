using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
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
///         The Email and App checks both run, in that order, and a request that fails either one
///         activates nothing — the pending row stays pending and can be confirmed by a later attempt.
///         The email code goes first because checking it spends nothing, where an app code that
///         verified is spent (<see cref="ITotpCodeVerifier" />): a wrong email code therefore leaves
///         the app code usable for the retry. A request whose Email check passed and whose App check
///         then failed leaves the email code unspent too, since it is consumed only once every check
///         has passed.
///     </para>
///     <para>
///         <b>One refusal, one budget.</b> A missing or wrong code for either method answers the one
///         re-authentication refusal (<c>AuthMessages.PasswordOrCodeIncorrect</c>, 401), and every
///         confirmation is charged to the account's re-authentication budget
///         (<see cref="ReauthenticationBudget" />, FR-AU-13), so a bearer token alone cannot guess
///         the pending factor's codes without bound, nor learn which of the two codes was wrong.
///     </para>
///     <para>
///         <b>Write order.</b> The recovery codes are persisted <em>before</em>
///         <see cref="TwoFactorAuth.IsActive" /> is set, and in a single
///         <see cref="IAsyncRepository{T, TKey}.CreateRangeAsync" /> rather than ten separate inserts. The
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
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<TwoFactorAuth, long> twoFactorReader,
    IAsyncRepository<TwoFactorAuth, long> twoFactorWriter,
    IAsyncReadOnlyRepository<TwoFactorEmailCode, long> emailCodeReader,
    IAtomicWrites atomicWrites,
    IAsyncReadOnlyRepository<TwoFactorRecoveryCode, long> recoveryCodeReader,
    IAsyncRepository<TwoFactorRecoveryCode, long> recoveryCodeWriter,
    ITotpCodeVerifier totpCodeVerifier)
    : ICommandHandlerAsync<ConfirmTwoFactorAuthCommand, ConfirmTwoFactorAuthCommandOutput>
{
    public async Task<DataOutput<ConfirmTwoFactorAuthCommandOutput?>> HandleAsync(
        ConfirmTwoFactorAuthCommand command, CancellationToken cancellationToken = default)
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

        if (person is null || twoFactorAuth is null)
        {
            return output.WithError(TwoFactorMessages.NoPendingSetup);
        }

        // AF-37d.
        if (twoFactorAuth.IsActive)
        {
            return output.WithError(TwoFactorMessages.AlreadyActive);
        }

        // FR-AU-13: the codes are a re-authentication like any other made under a bearer token, so
        // the check is charged to the account's budget before anything is compared. A paused budget
        // still has the codes compared, and refuses them whatever the result.
        var admitted = await ReauthenticationBudget.TryChargeAsync(atomicWrites, person);

        // AF-37c: the Email method's code, if the method was selected. Checked first, because looking
        // an email code up spends nothing but its guess budget, where an app code that verifies is
        // spent (FR-2F-14): the other order let a wrong email code burn a right app code.
        TwoFactorEmailCode? consumedEmailCode = null;

        if (twoFactorAuth.EmailEnabled)
        {
            consumedEmailCode = await FindMatchingEmailCodeAsync(twoFactorAuth.Id, command.EmailCode);

            if (consumedEmailCode is null)
            {
                return output.WithError(AuthMessages.PasswordOrCodeIncorrect);
            }
        }

        // AF-37b: the App method's code, if the method was selected. ITotpCodeVerifier owns the
        // secret, the clock-drift window, and the single-use rule, shared with UC-38/39/40's
        // ITwoFactorFactorVerifier.
        if (twoFactorAuth.AppEnabled && !await totpCodeVerifier.VerifyAsync(twoFactorAuth, command.AppCode))
        {
            return output.WithError(AuthMessages.PasswordOrCodeIncorrect);
        }

        // Only now is the pause decided, so that it answers as a wrong code does, and before the
        // email code is spent, so a paused request leaves it for the account holder.
        if (!admitted)
        {
            return output.WithError(AuthMessages.PasswordOrCodeIncorrect);
        }

        // UC-37 step 7, brought forward: the code is spent before anything is issued, in a write only
        // one request can win, so two confirmations racing on the same code cannot both issue a set
        // of recovery codes and activate.
        if (consumedEmailCode is not null && !await atomicWrites.TryConsumeEmailCodeAsync(consumedEmailCode))
        {
            return output.WithError(AuthMessages.PasswordOrCodeIncorrect);
        }

        await ReauthenticationBudget.ClearAsync(atomicWrites, person);

        // FR-AU-11: every token issued before the second factor was required is revoked — including
        // the one confirming it, so the caller signs in again, this time through UC-38. A token that
        // predates the factor proves only the password, which is what the factor guards against.
        // Before the writes that activate it, so a failure among them costs one extra sign-in at most.
        await atomicWrites.RotateSecurityStampAsync(person);

        // UC-37 steps 4-5 (FR-2F-05, FR-2F-20): ten fresh recovery codes, hashed at rest, returned
        // once — written before the activation below, per the write-order note in the remarks.
        var recoveryCodes = RecoveryCodes.Issue();

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

        return output
            .WithData(new ConfirmTwoFactorAuthCommandOutput
            {
                Enabled = true, RecoveryCodes = recoveryCodes.Select(issued => issued.Code).ToList()
            })
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
            emailCodeReader, atomicWrites, twoFactorAuthId, emailCode);

    /// <summary>
    ///     Persists the digests of <paramref name="recoveryCodes" /> as the configuration's whole
    ///     recovery code set, replacing anything already stored against it, in one insert. Returns the
    ///     persistence errors, or <c>null</c> on success.
    /// </summary>
    /// <remarks>
    ///     The delete is what makes a retry after a failed attempt safe: a prior run that stored its
    ///     codes and then failed to activate left ten rows nobody was ever told about, and without
    ///     this the next attempt would leave them in place alongside the ten it does return — the
    ///     same "old codes valid alongside new ones" UC-40 exists to prevent.
    /// </remarks>
    private async Task<IEnumerable<string>?> IssueRecoveryCodesAsync(
        long twoFactorAuthId, IReadOnlyCollection<IssuedRecoveryCode> recoveryCodes)
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
                TwoFactorAuthId = twoFactorAuthId, CodeHash = recoveryCode.CodeHash, Used = false
            }));

        return creation.Success ? null : creation.Errors;
    }
}
