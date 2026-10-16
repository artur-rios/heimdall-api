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
/// </remarks>
public class ResetPasswordCommandHandler(
    IValidator<ResetPasswordCommand> validator,
    IAsyncReadOnlyRepository<PasswordResetToken, long> tokenReader,
    IAsyncRepository<PasswordResetToken, long> tokenWriter,
    IAtomicWrites atomicWrites)
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

        // UC-13 step 4, for the presented token, brought forward and made atomic: the token is spent
        // before the password changes, by a write only one request can win. Two requests carrying
        // the same token both pass the checks above; only one may set a password with it, and the
        // other is told what is now true — the token was used (AF-13b).
        if (!await atomicWrites.TryConsumePasswordResetTokenAsync(token, now))
        {
            return output.WithError(AuthMessages.TokenAlreadyUsed);
        }

        // UC-13 step 3 and FR-AU-11, in one conditional statement: the new hash and salt and a
        // fresh security stamp, which revokes every token issued under the old password — a reset is
        // the remedy for a password someone else may know. Written only while the person is still as
        // found above (FR-PR-08, FR-PE-17); otherwise one of the account changes AF-13e refuses, or
        // an address change, landed in between, the link is answered as AF-13e would now answer it,
        // and nothing about the person changes, the stamp included. The token stays spent. One
        // statement also means no sign-in can read the new password beside the old stamp, so no
        // second rotation is needed.
        if (!await atomicWrites.TrySetPasswordAsync(person, newHash, newSalt, now))
        {
            return output.WithError(AuthMessages.TokenInvalid);
        }

        // UC-13 step 4, for every other token the person still holds.
        var consumption = await ConsumeTokensAsync(token, now);

        if (consumption is not null)
        {
            return output.WithErrors(consumption);
        }

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
    ///     Already-expired tokens are left alone. They are refused by AF-13a either way, and
    ///     rewriting them would only make a dead token report a different reason for being dead.
    /// </remarks>
    private async Task<IEnumerable<string>?> ConsumeTokensAsync(PasswordResetToken token, DateTime now)
    {
        var live = await tokenReader.Query()
            .Where(x => x.PersonId == token.PersonId && !x.Used && x.ExpiresAt > now)
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
