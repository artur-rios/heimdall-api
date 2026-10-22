using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="VerifyEmailCommand" /> (UC-14): finds the token UC-06 issued at person
///     creation, refuses it if it is unknown, if its account may no longer be acted on, or if it is
///     expired or spent (AF-14c/AF-14d/AF-14a/AF-14b), then marks the person's address as verified
///     and consumes the token (FR-EV-03). A token that matches none of those may instead belong to a
///     UC-47 pending registration, which <see cref="IPendingRegistrationConfirmer" /> turns into the
///     verified <c>User</c> it described when the command also carries the password chosen at sign-up
///     (a missing or wrong one is refused without consuming the registration); only when it matches
///     neither is it AF-14c. The password is read for pending registrations only and ignored for an
///     ordinary verification token.
/// </summary>
/// <remarks>
///     <para>
///         The mirror of <see cref="ResetPasswordCommandHandler" />, and it takes the same positions
///         for the same reasons. The token is the whole of the caller's claim — no email, no scope —
///         so the four rejections are checked in the order UC-14's main flow states: exists, its person
///         may still be acted on (answered as "exists" fails), not expired, not used. The token's own
///         three are named separately, unlike UC-11's uniform 401, because there is no account to
///         enumerate here: the token is a 48-character random string, and a caller either holds one
///         that was mailed to them or holds nothing.
///     </para>
///     <para>
///         An address that is already verified is verified again, and the token spent. UC-14 defines
///         no alternative flow for it — AF-15a rejects a *request* for another verification email, a
///         different thing — so refusing here would mean inventing one.
///     </para>
///     <para>
///         <b>The account is checked when the link is followed</b> (AF-14d, FR-EV-08), by the rule
///         UC-12 and UC-13 use (<see cref="PasswordRecoveryEligibility" />): a person restricted,
///         deleted or anonymised, or who lost the scope they sign in through, since the link was
///         mailed is answered exactly as an unknown token is (AF-14c), and nothing is written — a
///         restricted record is preserved as NFR-24 requires. The flag is set by one conditional
///         statement (<see cref="IAtomicWrites.TryMarkEmailVerifiedAsync" />) that repeats those
///         checks and requires the address the person was read with, so a change that lands in
///         between wins, and a save of the stale copy cannot put an old address back.
///     </para>
///     <para>
///         <b>The token is spent only with the write it unlocks</b> (#209), as in
///         <see cref="ResetPasswordCommandHandler" />: spending it and setting the flag share one
///         transaction, so a failed write leaves the link usable, while a write refused because the
///         person changed leaves the token spent. Retiring the person's other links runs once that has
///         committed, and a failure there is logged, not reported.
///     </para>
/// </remarks>
public class VerifyEmailCommandHandler(
    IValidator<VerifyEmailCommand> validator,
    IAsyncReadOnlyRepository<EmailVerificationToken, long> tokenReader,
    IAsyncRepository<EmailVerificationToken, long> tokenWriter,
    IAtomicWrites atomicWrites,
    IPendingRegistrationConfirmer pendingRegistrations,
    IWriteTransactions transactions,
    ILogger<VerifyEmailCommandHandler> logger)
    : ICommandHandlerAsync<VerifyEmailCommand, VerifyEmailCommandOutput>
{
    public async Task<DataOutput<VerifyEmailCommandOutput?>> HandleAsync(VerifyEmailCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<VerifyEmailCommandOutput?>.New;

        // Validate input shape (NFR-10). UC-14 names no alternative flow for it; an absent token
        // would answer 400 as AF-14c anyway.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        var now = DateTime.UtcNow;

        // UC-14 step 2. Matched by hash rather than by value, for the reason given in
        // ResetPasswordCommandHandler and in SingleUseTokenHash: the token is not stored.
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

        // AF-14c — unless the token is a UC-47 pending registration's, in which case following the link
        // is what creates the account, already verified.
        if (token is null)
        {
            var confirmation = await pendingRegistrations.ConfirmAsync(presented, command.Password, now);

            return confirmation.Outcome switch
            {
                PendingRegistrationOutcome.Confirmed => output
                    .WithData(new VerifyEmailCommandOutput())
                    .WithMessage(AuthMessages.EmailVerifiedSuccessfully),
                PendingRegistrationOutcome.Expired => output.WithError(AuthMessages.TokenExpired),
                PendingRegistrationOutcome.AlreadyClaimed => output.WithError(AuthMessages.TokenAlreadyUsed),
                PendingRegistrationOutcome.PasswordRequired => output.WithError(AuthMessages.ConfirmationPasswordRequired),
                PendingRegistrationOutcome.PasswordIncorrect => output.WithError(AuthMessages.ConfirmationPasswordIncorrect),
                PendingRegistrationOutcome.Failed => output.WithErrors(confirmation.Errors),
                _ => output.WithError(AuthMessages.TokenInvalid)
            };
        }

        // AF-14d (FR-EV-08): the account must still be one that may be acted on, answered as AF-14c
        // whatever the reason, and before the token's own state.
        if (PasswordRecoveryEligibility.Of(token.Person) != RecoveryEligibility.Eligible)
        {
            return output.WithError(AuthMessages.TokenInvalid);
        }

        // AF-14a.
        if (token.ExpiresAt <= now)
        {
            return output.WithError(AuthMessages.TokenExpired);
        }

        // AF-14b.
        if (token.Used)
        {
            return output.WithError(AuthMessages.TokenAlreadyUsed);
        }

        // The token and the flag it unlocks land together or not at all (#209).
        await using (var transaction = await transactions.BeginAsync())
        {
            // UC-14 step 4, for the presented token, brought forward and made atomic: of two requests
            // carrying the same token, only the one that spends it verifies the address (AF-14b for
            // the other).
            if (!await atomicWrites.TryConsumeEmailVerificationTokenAsync(token, now))
            {
                return output.WithError(AuthMessages.TokenAlreadyUsed);
            }

            // UC-14 step 3 (FR-EV-03), written only while the person is still as found above
            // (FR-EV-08, FR-PE-17). Otherwise the link is answered as AF-14d would now answer it.
            var written = await atomicWrites.TryMarkEmailVerifiedAsync(token.Person, now);

            // Committed either way: a refused write is an answer, and the token stays spent. Only a
            // write that fails leaves the transaction to roll back.
            await transaction.CommitAsync();

            if (!written)
            {
                return output.WithError(AuthMessages.TokenInvalid);
            }
        }

        // UC-14 step 4, for every other token the person still holds.
        await RetireOtherTokensAsync(token, now);

        // UC-14 step 5.
        return output
            .WithData(new VerifyEmailCommandOutput())
            .WithMessage(AuthMessages.EmailVerifiedSuccessfully);
    }

    /// <summary>
    ///     UC-14 step 4, applied to every verification token the person still holds rather than only
    ///     the one presented. UC-06 issues one at creation and UC-15 issues more on request, so a
    ///     person can hold several live links; once one of them has verified the address, the rest
    ///     verify an address that is already verified and are worth nothing but the room they take up
    ///     in a mailbox.
    /// </summary>
    /// <remarks>
    ///     This is not UC-15 step 3, which retires outstanding tokens before issuing a new one. That
    ///     remains UC-15's own work.
    ///     <para>
    ///         Already-expired tokens are left alone. They are refused by AF-14a either way, and
    ///         rewriting them would only make a dead token report a different reason for being dead.
    ///     </para>
    ///     <para>
    ///         Best effort (#209): the address is already verified, so a failure here is logged and not
    ///         reported. It runs in a transaction of its own so that a failure retires none of them and
    ///         leaves nothing pending for the next save, the audit entry's.
    ///     </para>
    /// </remarks>
    private async Task RetireOtherTokensAsync(EmailVerificationToken token, DateTime now)
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
                    logger.LogWarning("The other verification links of a verified address could not be retired: {Errors}",
                        string.Join(" | ", update.Errors));

                    return;
                }
            }

            await transaction.CommitAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The other verification links of a verified address could not be retired");
        }
    }
}
