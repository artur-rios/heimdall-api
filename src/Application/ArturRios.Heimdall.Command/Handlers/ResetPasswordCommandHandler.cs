using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="ResetPasswordCommand" /> (UC-13): finds the token UC-12 issued, refuses it
///     if it is unknown, if its account may no longer be acted on, or if it is expired or spent
///     (AF-13c/AF-13e/AF-13a/AF-13b, FR-PR-04, FR-PR-08), then re-hashes the new password under a
///     freshly generated salt and consumes the token (FR-PR-03).
/// </summary>
/// <remarks>
///     <para>
///         The token is the whole of the caller's claim — no email, no scope, no password is asked
///         for — so the four rejections are checked in the order UC-13's main flow states: exists,
///         its person may still be acted on (answered as "exists" fails), not expired, not used. The
///         token's own three are named separately, unlike UC-11's uniform 401, because there is no
///         account to enumerate here: the token is a 48-character random string, and a caller either
///         holds one that was mailed to them or holds nothing.
///     </para>
///     <para>
///         <b>The account is checked again when the link is followed</b> (AF-13e, FR-PR-08). UC-12
///         and UC-52 decide who a link is worth sending to (<see cref="PasswordRecoveryEligibility" />),
///         but a link lives for an hour, and the person can be restricted, deleted or anonymised, or
///         lose the scope they sign in through, while it waits in a mailbox. The same rule is run
///         at redemption, and a person it no longer admits is answered exactly as an unknown token is
///         (AF-13c), with nothing written: the token stays as it was, and a restricted record is
///         preserved as NFR-24 requires. The answer reveals nothing a caller holding a dead link did
///         not already have.
///     </para>
///     <para>
///         <b>The password is written by one conditional statement</b>
///         (<see cref="IAtomicWrites.TrySetPasswordAsync" />), not by saving the person read with
///         the token. The condition repeats the state checks against the row as it is at that moment
///         and requires the address the person was read with, so a restriction, a deletion or an
///         address change that lands between the check and the write wins (FR-PE-17), and a save of
///         the stale copy cannot put the old address back.
///     </para>
///     <para>
///         <b>The token is spent only with the write it unlocks</b> (#209). Spending it and writing the
///         password share one transaction (<see cref="IWriteTransactions" />): spending it first is what
///         lets only one of two requests carrying it go on, and the transaction is what stops a failed
///         write from taking the token with it, so the person can follow the same link again. A write
///         refused because the person changed is not a failure, and the token stays spent (FR-PR-08).
///         Retiring the person's other links runs after that transaction has committed: it is clean-up
///         after a reset that has happened, so a failure there is logged and the reset is still
///         reported as done.
///     </para>
/// </remarks>
public class ResetPasswordCommandHandler(
    IValidator<ResetPasswordCommand> validator,
    IAsyncReadOnlyRepository<PasswordResetToken, long> tokenReader,
    IAsyncRepository<PasswordResetToken, long> tokenWriter,
    IAtomicWrites atomicWrites,
    IWriteTransactions transactions,
    ILogger<ResetPasswordCommandHandler> logger)
    : ICommandHandlerAsync<ResetPasswordCommand, ResetPasswordCommandOutput>
{
    public async Task<DataOutput<ResetPasswordCommandOutput?>> HandleAsync(ResetPasswordCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<ResetPasswordCommandOutput?>.New;

        // AF-13d: validate input shape (NFR-10).
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        var now = DateTime.UtcNow;

        // UC-13 step 2. Matched by hash, because the token itself is not stored (TH-14): the
        // presented value is hashed the same way the issued one was and looked up against the unique
        // index on the digest. Still an exact, case-sensitive match — hashing preserves that, since
        // any difference in the input changes the digest.
        var presented = SingleUseTokenHash.Of(command.Token);

        // Both scope navigations are loaded: the eligibility rule reads them.
        var token = await tokenReader.Query()
            .Include(x => x.Person)
            .ThenInclude(person => person.ScopeMembership!)
            .ThenInclude(membership => membership.Scope)
            .Include(x => x.Person)
            .ThenInclude(person => person.ScopeOwnerships)
            .ThenInclude(ownership => ownership.Scope)
            .FirstOrDefaultAsync(x => x.TokenHash == presented);

        // AF-13c.
        if (token is null)
        {
            return output.WithError(AuthMessages.TokenInvalid);
        }

        // AF-13e (FR-PR-08): UC-12's rule, run again now the link is followed, and answered as
        // AF-13c whatever the reason, before the token's own state, so a link for an account that
        // may no longer be acted on says nothing more than an unknown one.
        if (PasswordRecoveryEligibility.Of(token.Person) != RecoveryEligibility.Eligible)
        {
            return output.WithError(AuthMessages.TokenInvalid);
        }

        // AF-13a.
        if (token.ExpiresAt <= now)
        {
            return output.WithError(AuthMessages.TokenExpired);
        }

        // AF-13b.
        if (token.Used)
        {
            return output.WithError(AuthMessages.TokenAlreadyUsed);
        }

        // UC-13 step 3 (FR-PR-03, FR-RO-04, NFR-02): a new random salt, not the person's existing
        // one, so the stored hash shares nothing with the one it replaces.
        var person = token.Person;

        var (newHash, newSalt) = await PasswordHashGate.Shared.EncodeWithRandomSaltAsync(command.NewPassword);

        // The token and the password it unlocks land together or not at all (#209). Opened after the
        // hash, so no row lock waits on Argon2.
        await using (var transaction = await transactions.BeginAsync())
        {
            // UC-13 step 4, for the presented token, brought forward and made atomic: the token is
            // spent before the password changes, by a write only one request can win. Two requests
            // carrying the same token both pass the checks above; only one may set a password with it,
            // and the other is told what is now true — the token was used (AF-13b).
            if (!await atomicWrites.TryConsumePasswordResetTokenAsync(token, now))
            {
                return output.WithError(AuthMessages.TokenAlreadyUsed);
            }

            // UC-13 step 3 and FR-AU-11, in one conditional statement: the new hash and salt and a
            // fresh security stamp, which revokes every token issued under the old password — a reset
            // is the remedy for a password someone else may know. Written only while the person is
            // still as found above (FR-PR-08, FR-PE-17); otherwise one of the account changes AF-13e
            // refuses, or an address change, landed in between, the link is answered as AF-13e would
            // now answer it, and nothing about the person changes, the stamp included. One statement
            // also means no sign-in can read the new password beside the old stamp, so no second
            // rotation is needed.
            var written = await atomicWrites.TrySetPasswordAsync(person, newHash, newSalt, now);

            // Committed whether or not the password was written: a write refused because the person
            // changed is an answer, not a failure, and the token stays spent (FR-PR-08). Only a write
            // that fails — an exception, here — leaves the transaction to roll back.
            await transaction.CommitAsync();

            if (!written)
            {
                return output.WithError(AuthMessages.TokenInvalid);
            }
        }

        // UC-13 step 4, for every other token the person still holds.
        await RetireOtherTokensAsync(token, now);

        // UC-13 step 5.
        return output
            .WithData(new ResetPasswordCommandOutput())
            .WithMessage(AuthMessages.PasswordResetSuccessful);
    }

    /// <summary>
    ///     UC-13 step 4, applied to every reset token the person still holds rather than only the one
    ///     presented. UC-12 issues a token per request without retiring the last, so a person who
    ///     clicked "forgot password" twice has two live links. Once one of them has changed the
    ///     password, the others are exactly what this use case exists to expire: a way to change it
    ///     again, from an inbox that may be the reason the reset was needed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Already-expired tokens are left alone. They are refused by AF-13a either way, and
    ///         rewriting them would only make a dead token report a different reason for being dead.
    ///     </para>
    ///     <para>
    ///         Best effort (#209): the password has already been reset, so a failure here is logged and
    ///         not reported — the links it leaves live expire within the hour. It runs in a transaction
    ///         of its own so that a failure retires none of them and leaves nothing pending for the
    ///         next save, the audit entry's.
    ///     </para>
    /// </remarks>
    private async Task RetireOtherTokensAsync(PasswordResetToken token, DateTime now)
    {
        try
        {
            await using var transaction = await transactions.BeginAsync();

            var live = await tokenReader.Query()
                .Where(x => x.PersonId == token.PersonId && !x.Used && x.ExpiresAt > now)
                .ToListAsync();

            foreach (var outstanding in live)
            {
                outstanding.Used = true;

                var update = await tokenWriter.UpdateAsync(outstanding);

                if (!update.Success)
                {
                    logger.LogWarning("The other password reset links of a reset password could not be retired: {Errors}",
                        string.Join(" | ", update.Errors));

                    return;
                }
            }

            await transaction.CommitAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The other password reset links of a reset password could not be retired");
        }
    }
}
