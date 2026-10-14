using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Command.Services;

public enum PendingRegistrationOutcome
{
    NotFound, Expired, AlreadyClaimed, Refused, Failed, Confirmed, PasswordRequired, PasswordIncorrect
}

public sealed record PendingRegistrationConfirmation(
    PendingRegistrationOutcome Outcome, IReadOnlyList<string> Errors)
{
    public static PendingRegistrationConfirmation Of(PendingRegistrationOutcome outcome) => new(outcome, []);
}

/// <summary>
///     UC-47 confirmation, reached through UC-14 when a presented token names a pending registration
///     rather than a verification token: creates the verified <c>User</c> the registration described,
///     once the caller also presents the password chosen at sign-up (spec §4.3a).
/// </summary>
public interface IPendingRegistrationConfirmer
{
    Task<PendingRegistrationConfirmation> ConfirmAsync(string tokenHash, string? password, DateTime now);
}

/// <remarks>
///     <para>
///         The row is claimed by deleting it before anything is created, so of two requests carrying one
///         token only one can create the person. The scope and the address are re-checked because either
///         may have changed since the registration was queued; the per-scope unique index is the last
///         guard against two instances confirming one address at once.
///     </para>
///     <para>
///         The link alone does not confirm: the password chosen at sign-up must accompany it, so a
///         squatter who registered someone else's address cannot be handed a working login by a scanner, a
///         mistaken click or the wrong one of two identical emails. A missing or wrong password is refused
///         after the expiry check and before the claim, so it consumes nothing and the registrant may try
///         again. Each guess is charged against the row before it is compared, by one conditional write
///         (<see cref="IAtomicWrites" />, NFR-27), so parallel guesses cannot outrun the budget of ten:
///         the tenth wrong password discards the row, the answer for that attempt is still "incorrect",
///         and the next attempt finds no row. A row found with the budget already spent is discarded
///         without a comparison.
///     </para>
///     <para>
///         With parallel requests the discard can come early: a wrong guess charged before the tenth may
///         finish after it and, finding the stored count at ten, delete the row while the tenth guess,
///         the correct one, is still being compared. That guess then finds the row gone at the claim
///         and answers "already used" (<see cref="PendingRegistrationOutcome.AlreadyClaimed" />). It is
///         the safe side of the race, since nothing is created and no more than ten guesses were ever
///         compared, so the cap holds.
///     </para>
///     <para>
///         Every failure is logged with a fixed message and no address (NFR-22), and this class
///         throws none itself: the outcome is the caller's to turn into an answer. What can still surface
///         is the <c>PasswordHashGateSaturatedException</c> of a saturated <c>PasswordHashGate</c> during
///         the password check, which <c>PasswordHashSaturationFilter</c> answers with 503.
///     </para>
/// </remarks>
public class PendingRegistrationConfirmer(
    IAsyncReadOnlyRepository<PendingRegistration, long> pendingReader,
    IAsyncRepository<PendingRegistration, long> pendingWriter,
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<GoogleUser, long> googleUserReader,
    IAsyncRepository<Person, long> personWriter,
    IAtomicWrites atomicWrites,
    ILogger<PendingRegistrationConfirmer> logger)
    : IPendingRegistrationConfirmer
{
    public async Task<PendingRegistrationConfirmation> ConfirmAsync(
        string tokenHash, string? password, DateTime now)
    {
        var pending = await pendingReader.Query()
            .Include(x => x.Scope)
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash);

        if (pending is null)
        {
            return PendingRegistrationConfirmation.Of(PendingRegistrationOutcome.NotFound);
        }

        if (pending.ExpiresAt <= now)
        {
            return PendingRegistrationConfirmation.Of(PendingRegistrationOutcome.Expired);
        }

        // Following the link is not enough: a scanner, a mistaken click or the wrong one of two identical
        // emails would otherwise create an account whose password the registrant chose (spec §4.3a, 2).
        if (string.IsNullOrEmpty(password))
        {
            return PendingRegistrationConfirmation.Of(PendingRegistrationOutcome.PasswordRequired);
        }

        // The guess is paid for before it is compared, by the write that checks the budget (NFR-27): of
        // any number of simultaneous requests only as many reach the comparison as the budget allows. A
        // spent budget discards the row without comparing, so a right password is no better than a wrong
        // one once the guesses are gone.
        if (!await atomicWrites.TryChargePendingRegistrationGuessAsync(pending, PendingRegistration.MaxConfirmationGuesses))
        {
            await DiscardAsync(pending);

            return PendingRegistrationConfirmation.Of(PendingRegistrationOutcome.Refused);
        }

        if (!await PasswordHashGate.Shared.TextMatchesAsync(password, pending.PasswordHash, pending.Salt))
        {
            // Asked of the database, not read off the instance: the instance holds the count as this
            // request first read it plus its own charge, which under parallel guesses is behind the
            // stored one. The count only rises, so whichever wrong guess finishes last sees ten and
            // discards the row, however the ten interleaved.
            if (await pendingReader.Query().AnyAsync(x => x.Id == pending.Id && x.FailedAttempts >= PendingRegistration.MaxConfirmationGuesses))
            {
                await DiscardAsync(pending);
            }

            return PendingRegistrationConfirmation.Of(PendingRegistrationOutcome.PasswordIncorrect);
        }

        var claim = await pendingWriter.DeleteRangeAsync([pending.Id]);

        if (!claim.Success)
        {
            // Another request deleted the row between this delete's re-read and its commit: EF reports the
            // lost race as a concurrency failure. It is the same outcome as finding the row already gone,
            // not a fault, so a double submit answers "already used" rather than a conflict.
            if (claim.Errors.Contains(RelationalErrors.ConcurrencyMessage))
            {
                return PendingRegistrationConfirmation.Of(PendingRegistrationOutcome.AlreadyClaimed);
            }

            logger.LogError("A pending registration could not be claimed: {Errors}", string.Join(" | ", claim.Errors));
            return new(PendingRegistrationOutcome.Failed, claim.Errors.ToList());
        }

        if ((claim.Data?.Count() ?? 0) == 0)
        {
            return PendingRegistrationConfirmation.Of(PendingRegistrationOutcome.AlreadyClaimed);
        }

        var scope = pending.Scope;

        if (scope.IsDeleted || !scope.SelfRegistrationEnabled || await AddressTakenAsync(pending))
        {
            return PendingRegistrationConfirmation.Of(PendingRegistrationOutcome.Refused);
        }

        var person = new Person
        {
            Name = pending.Name,
            Email = pending.Email,
            PasswordHash = pending.PasswordHash,
            Salt = pending.Salt,
            RoleId = (long)Roles.User,
            EmailVerified = true,
            ScopeMembership = new ScopeUser { ScopeId = scope.Id },
            ScopeId = scope.Id
        };

        LegalBasisRecorder.Record(person, scope, now);

        var creation = await personWriter.CreateAsync(person);

        if (!creation.Success)
        {
            if (creation.Errors.Contains(RelationalErrors.UniqueViolationMessage))
            {
                return PendingRegistrationConfirmation.Of(PendingRegistrationOutcome.Refused);
            }

            logger.LogError("A confirmed registration could not be stored: {Errors}", string.Join(" | ", creation.Errors));
            return new(PendingRegistrationOutcome.Failed, creation.Errors.ToList());
        }

        await RemoveOtherPendingAsync(pending);

        return PendingRegistrationConfirmation.Of(PendingRegistrationOutcome.Confirmed);
    }

    /// <summary>
    ///     Deletes a row whose guess budget is spent, so a leaked link cannot be used to guess the sign-up
    ///     password without limit. The answer for the attempt does not depend on whether this succeeds:
    ///     the budget is spent either way, so no further comparison is admitted. A delete that loses a
    ///     race to another request's is the row already gone, not a fault.
    /// </summary>
    private async Task DiscardAsync(PendingRegistration pending)
    {
        var removal = await pendingWriter.DeleteRangeAsync([pending.Id]);

        if (!removal.Success && !removal.Errors.Contains(RelationalErrors.ConcurrencyMessage))
        {
            // Fixed message, no address (NFR-22). The row stays, but it admits no further comparison.
            logger.LogError("A pending registration with no guesses left could not be discarded");
        }
    }

    private async Task<bool> AddressTakenAsync(PendingRegistration pending)
    {
        var email = pending.Email.ToLower();

        return await personReader.Query().AnyAsync(person =>
                   !person.IsDeleted && person.Email.ToLower() == email &&
                   person.ScopeMembership != null && person.ScopeMembership.ScopeId == pending.ScopeId) ||
               await googleUserReader.Query().AnyAsync(googleUser =>
                   googleUser.ScopeId == pending.ScopeId && googleUser.Email.ToLower() == email);
    }

    private async Task RemoveOtherPendingAsync(PendingRegistration confirmed)
    {
        var email = confirmed.Email.ToLower();

        var others = await pendingReader.Query()
            .Where(x => x.ScopeId == confirmed.ScopeId && x.Email.ToLower() == email)
            .Select(x => x.Id)
            .ToListAsync();

        if (others.Count == 0)
        {
            return;
        }

        var removal = await pendingWriter.DeleteRangeAsync(others);

        if (!removal.Success)
        {
            // Harmless if it fails: the rows expire and are purged, and confirming one later is refused
            // because the address is now taken.
            logger.LogWarning("Superseded pending registrations could not be removed: {Errors}",
                string.Join(" | ", removal.Errors));
        }
    }
}
