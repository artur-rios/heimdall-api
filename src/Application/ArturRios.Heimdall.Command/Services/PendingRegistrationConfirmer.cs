using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Command.Services;

public enum PendingRegistrationOutcome { NotFound, Expired, AlreadyClaimed, Refused, Failed, Confirmed }

public sealed record PendingRegistrationConfirmation(
    PendingRegistrationOutcome Outcome, IReadOnlyList<string> Errors)
{
    public static PendingRegistrationConfirmation Of(PendingRegistrationOutcome outcome) => new(outcome, []);
}

/// <summary>
///     UC-47 confirmation, reached through UC-14 when a presented token names a pending registration
///     rather than a verification token: creates the verified <c>User</c> the registration described.
/// </summary>
public interface IPendingRegistrationConfirmer
{
    Task<PendingRegistrationConfirmation> ConfirmAsync(string tokenHash, DateTime now);
}

/// <remarks>
///     <para>
///         The row is claimed by deleting it before anything is created, so of two requests carrying one
///         token only one can create the person. The scope and the address are re-checked because either
///         may have changed since the registration was queued; the per-scope unique index is the last
///         guard against two instances confirming one address at once.
///     </para>
///     <para>
///         Every failure is logged with a fixed message and no address (NFR-22), and none is thrown: the
///         outcome is the caller's to turn into an answer.
///     </para>
/// </remarks>
public class PendingRegistrationConfirmer(
    IAsyncReadOnlyRepository<PendingRegistration, long> pendingReader,
    IAsyncRepository<PendingRegistration, long> pendingWriter,
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<GoogleUser, long> googleUserReader,
    IAsyncRepository<Person, long> personWriter,
    ILogger<PendingRegistrationConfirmer> logger)
    : IPendingRegistrationConfirmer
{
    public async Task<PendingRegistrationConfirmation> ConfirmAsync(string tokenHash, DateTime now)
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
