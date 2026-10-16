using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Data.Persistence;

/// <summary>
///     <see cref="IAtomicWrites" /> on EF Core's <c>ExecuteUpdate</c>: each method is a single
///     <c>UPDATE … WHERE</c> whose condition is the rule being enforced, and whose affected-row count
///     says whether this caller won.
/// </summary>
/// <remarks>
///     <para>
///         PostgreSQL makes the condition and the write one step: under READ COMMITTED, an
///         <c>UPDATE</c> that waits on another transaction's lock re-evaluates its <c>WHERE</c>
///         against the row as that transaction left it. So two requests spending the same code both
///         reach the row, one writes, and the other finds "unused" no longer true and writes nothing.
///         No row version is needed, and no table changes shape for it.
///     </para>
///     <para>
///         <c>ExecuteUpdate</c> bypasses the change tracker, which would leave the caller's tracked
///         instance stale — and a later save would write the stale values back over the atomic ones.
///         <see cref="Accept{T,TProperty}" /> sets the instance's current and original values to what
///         was written, so the tracker sees nothing to save.
///     </para>
/// </remarks>
public sealed class AtomicWrites(AppDbContext context) : IAtomicWrites
{
    public async Task<bool> TryReserveLoginAttemptAsync(
        Person person, int maxAttempts, DateTime now, DateTime lockedOutUntil)
    {
        // One statement, so the attempt that reaches the limit is also the one that locks: every
        // right-hand side reads the row as it was before the update. A request that dies after this
        // returns — shed by the hash gate, or cut off by a database fault — has still left a lock that
        // expires, and not a counter at the limit that nothing could ever reset. A row already at or
        // past the limit with no lockout date (left by an older instance, or by a lowered limit) is
        // admitted too: the same >= test locks it and resets the counter, so it heals itself.
        var reserved = await context.Persons
            .Where(x => x.Id == person.Id &&
                        ((x.FailedLoginAttempts < maxAttempts &&
                          (x.LockedOutUntil == null || x.LockedOutUntil <= now)) ||
                         (x.FailedLoginAttempts >= maxAttempts && x.LockedOutUntil == null)))
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.LockedOutUntil,
                    x => x.FailedLoginAttempts + 1 >= maxAttempts ? lockedOutUntil : x.LockedOutUntil)
                .SetProperty(x => x.FailedLoginAttempts,
                    x => x.FailedLoginAttempts + 1 >= maxAttempts ? 0 : x.FailedLoginAttempts + 1)) == 1;

        if (reserved)
        {
            var reachedLimit = person.FailedLoginAttempts + 1 >= maxAttempts;

            if (reachedLimit)
            {
                Accept(person, x => x.LockedOutUntil, lockedOutUntil);
            }

            Accept(person, x => x.FailedLoginAttempts, reachedLimit ? 0 : person.FailedLoginAttempts + 1);
        }

        return reserved;
    }

    public async Task<bool> TryChargePendingRegistrationGuessAsync(PendingRegistration pending, int maxGuesses)
    {
        var charged = await context.PendingRegistrations
            .Where(x => x.Id == pending.Id && x.FailedAttempts < maxGuesses)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.FailedAttempts, x => x.FailedAttempts + 1)) == 1;

        if (charged)
        {
            Accept(pending, x => x.FailedAttempts, pending.FailedAttempts + 1);
        }

        return charged;
    }

    public async Task ClearLoginAttemptsAsync(Person person)
    {
        await context.Persons
            .Where(x => x.Id == person.Id)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.FailedLoginAttempts, 0)
                .SetProperty(x => x.LockedOutUntil, (DateTime?)null));

        Accept(person, x => x.FailedLoginAttempts, 0);
        Accept(person, x => x.LockedOutUntil, null);
    }

    public async Task<bool> TryReserveSecondFactorAttemptAsync(
        Person person, int maxAttempts, DateTime now, IReadOnlyList<DateTime> lockedOutUntil)
    {
        ArgumentOutOfRangeException.ThrowIfZero(lockedOutUntil.Count, nameof(lockedOutUntil));

        // The login reservation's shape, for the same reasons: the reservation that reaches the limit
        // is the one that locks, and a row at or past the limit with no live lock — none at all, or
        // one that has run out, as a lowered limit would leave — is admitted and locked, so it heals
        // itself rather than being refused for good. What differs
        // is the lock's length, which depends on how many locks came before — so it is chosen by the
        // stored lockout count, inside the same statement, rather than from the count this request
        // read: two requests that both read "one lock so far" cannot both set the second lock's end.
        var reserved = await context.Persons
            .Where(x => x.Id == person.Id &&
                        ((x.FailedSecondFactorAttempts < maxAttempts &&
                          (x.SecondFactorLockedOutUntil == null || x.SecondFactorLockedOutUntil <= now)) ||
                         (x.FailedSecondFactorAttempts >= maxAttempts &&
                          (x.SecondFactorLockedOutUntil == null || x.SecondFactorLockedOutUntil <= now))))
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.SecondFactorLockedOutUntil, SecondFactorLockEnd(maxAttempts, lockedOutUntil))
                .SetProperty(x => x.SecondFactorLockouts,
                    x => x.FailedSecondFactorAttempts + 1 >= maxAttempts
                        ? x.SecondFactorLockouts + 1
                        : x.SecondFactorLockouts)
                .SetProperty(x => x.FailedSecondFactorAttempts,
                    x => x.FailedSecondFactorAttempts + 1 >= maxAttempts ? 0 : x.FailedSecondFactorAttempts + 1)) == 1;

        if (reserved)
        {
            var reachedLimit = person.FailedSecondFactorAttempts + 1 >= maxAttempts;

            if (reachedLimit)
            {
                Accept(person, x => x.SecondFactorLockedOutUntil, LockEndAfter(person.SecondFactorLockouts, lockedOutUntil));
                Accept(person, x => x.SecondFactorLockouts, person.SecondFactorLockouts + 1);
            }

            Accept(person, x => x.FailedSecondFactorAttempts, reachedLimit ? 0 : person.FailedSecondFactorAttempts + 1);
        }
        else
        {
            await TouchAsync(person);
        }

        return reserved;
    }

    public async Task ClearSecondFactorAttemptsAsync(Person person)
    {
        await context.Persons
            .Where(x => x.Id == person.Id)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.FailedSecondFactorAttempts, 0)
                .SetProperty(x => x.SecondFactorLockedOutUntil, (DateTime?)null)
                .SetProperty(x => x.SecondFactorLockouts, 0));

        Accept(person, x => x.FailedSecondFactorAttempts, 0);
        Accept(person, x => x.SecondFactorLockedOutUntil, null);
        Accept(person, x => x.SecondFactorLockouts, 0);
    }

    public async Task<bool> TryReserveReauthenticationAsync(
        Person person, int maxFailures, DateTime now, DateTime windowStartedBy, DateTime pausedUntil)
    {
        // One statement, every right-hand side reading the row as it was. The window is open when it
        // started after windowStartedBy (a NULL start is no window, and compares false); the failure
        // counted here is then the window's next, or else the first of a new window. The failure that
        // reaches the limit pauses and closes the window in the same write, as the login reservation
        // locks, so a burst cannot each read "below the limit" and pass it together.
        var reserved = await context.Persons
            .Where(x => x.Id == person.Id &&
                        (x.ReauthenticationPausedUntil == null || x.ReauthenticationPausedUntil <= now))
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.ReauthenticationPausedUntil,
                    x => (x.ReauthenticationWindowStartedAt > windowStartedBy
                        ? x.FailedReauthenticationAttempts + 1
                        : 1) >= maxFailures
                        ? (DateTime?)pausedUntil
                        : x.ReauthenticationPausedUntil)
                .SetProperty(x => x.ReauthenticationWindowStartedAt,
                    x => (x.ReauthenticationWindowStartedAt > windowStartedBy
                        ? x.FailedReauthenticationAttempts + 1
                        : 1) >= maxFailures
                        ? null
                        : x.ReauthenticationWindowStartedAt > windowStartedBy
                            ? x.ReauthenticationWindowStartedAt
                            : (DateTime?)now)
                .SetProperty(x => x.FailedReauthenticationAttempts,
                    x => (x.ReauthenticationWindowStartedAt > windowStartedBy
                        ? x.FailedReauthenticationAttempts + 1
                        : 1) >= maxFailures
                        ? 0
                        : x.ReauthenticationWindowStartedAt > windowStartedBy
                            ? x.FailedReauthenticationAttempts + 1
                            : 1)) == 1;

        if (reserved)
        {
            var windowOpen = person.ReauthenticationWindowStartedAt > windowStartedBy;
            var failures = windowOpen ? person.FailedReauthenticationAttempts + 1 : 1;
            var paused = failures >= maxFailures;

            if (paused)
            {
                Accept(person, x => x.ReauthenticationPausedUntil, pausedUntil);
            }

            Accept(person, x => x.ReauthenticationWindowStartedAt,
                paused ? null : windowOpen ? person.ReauthenticationWindowStartedAt : now);
            Accept(person, x => x.FailedReauthenticationAttempts, paused ? 0 : failures);
        }
        else
        {
            await TouchAsync(person);
        }

        return reserved;
    }

    public async Task<AccountEmailReservation> TryReserveAccountEmailAsync(
        Person person, int maxPerWindow, DateTime now, DateTime lastSentBefore, DateTime windowEndsAt)
    {
        // One statement, every right-hand side reading the row as it was. Admitted only out of the
        // cooldown and under the window's count; an ended (or never opened) window starts again at
        // this send. Concurrent reservations queue on the row lock and re-read it, so a burst gets
        // exactly what is left — one inside a cooldown, at most the cap with none.
        var reserved = await context.Persons
            .Where(x => x.Id == person.Id &&
                        (x.AccountEmailLastSentAt == null || x.AccountEmailLastSentAt <= lastSentBefore) &&
                        (x.AccountEmailWindowEndsAt == null || x.AccountEmailWindowEndsAt <= now ||
                         x.AccountEmailsSent < maxPerWindow))
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.AccountEmailsSent,
                    x => x.AccountEmailWindowEndsAt == null || x.AccountEmailWindowEndsAt <= now
                        ? 1
                        : x.AccountEmailsSent + 1)
                .SetProperty(x => x.AccountEmailWindowEndsAt,
                    x => x.AccountEmailWindowEndsAt == null || x.AccountEmailWindowEndsAt <= now
                        ? (DateTime?)windowEndsAt
                        : x.AccountEmailWindowEndsAt)
                .SetProperty(x => x.AccountEmailLastSentAt, (DateTime?)now)) == 1;

        if (reserved)
        {
            var windowOpen = person.AccountEmailWindowEndsAt > now;

            Accept(person, x => x.AccountEmailsSent, windowOpen ? person.AccountEmailsSent + 1 : 1);
            Accept(person, x => x.AccountEmailWindowEndsAt, windowOpen ? person.AccountEmailWindowEndsAt : windowEndsAt);
            Accept(person, x => x.AccountEmailLastSentAt, now);

            return AccountEmailReservation.Reserved;
        }

        // Refused: read the row as it now is, to say which bound refused it and when it ends, and bring
        // the instance in line with it. The daily one is the more useful answer when both apply, since
        // waiting out the cooldown would not help. A row gone since it was loaded (a hard delete in
        // between) is refused too: nothing may be sent to a person who no longer exists.
        var stored = await context.Persons
            .AsNoTracking()
            .Where(x => x.Id == person.Id)
            .Select(x => new { x.AccountEmailsSent, x.AccountEmailWindowEndsAt, x.AccountEmailLastSentAt })
            .SingleOrDefaultAsync();

        if (stored is null)
        {
            return AccountEmailReservation.CoolingDown;
        }

        Accept(person, x => x.AccountEmailsSent, stored.AccountEmailsSent);
        Accept(person, x => x.AccountEmailWindowEndsAt, stored.AccountEmailWindowEndsAt);
        Accept(person, x => x.AccountEmailLastSentAt, stored.AccountEmailLastSentAt);

        return stored.AccountEmailWindowEndsAt > now && stored.AccountEmailsSent >= maxPerWindow
            ? AccountEmailReservation.DailyLimitReached
            : AccountEmailReservation.CoolingDown;
    }

    public async Task ClearAccountEmailsAsync(Person person)
    {
        await context.Persons
            .Where(x => x.Id == person.Id)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.AccountEmailsSent, 0)
                .SetProperty(x => x.AccountEmailWindowEndsAt, (DateTime?)null)
                .SetProperty(x => x.AccountEmailLastSentAt, (DateTime?)null));

        Accept(person, x => x.AccountEmailsSent, 0);
        Accept(person, x => x.AccountEmailWindowEndsAt, null);
        Accept(person, x => x.AccountEmailLastSentAt, null);
    }

    public async Task ClearReauthenticationAttemptsAsync(Person person)
    {
        await context.Persons
            .Where(x => x.Id == person.Id)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.FailedReauthenticationAttempts, 0)
                .SetProperty(x => x.ReauthenticationWindowStartedAt, (DateTime?)null)
                .SetProperty(x => x.ReauthenticationPausedUntil, (DateTime?)null));

        Accept(person, x => x.FailedReauthenticationAttempts, 0);
        Accept(person, x => x.ReauthenticationWindowStartedAt, null);
        Accept(person, x => x.ReauthenticationPausedUntil, null);
    }

    public async Task StartChallengeAsync(TwoFactorAuth configuration, Guid challengeId)
    {
        await context.TwoFactorAuths
            .Where(x => x.Id == configuration.Id)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.ChallengeId, challengeId)
                .SetProperty(x => x.EmailCodeReissueCount, 0)
                .SetProperty(x => x.ChallengeAttempts, 0));

        Accept(configuration, x => x.ChallengeId, challengeId);
        Accept(configuration, x => x.EmailCodeReissueCount, 0);
        Accept(configuration, x => x.ChallengeAttempts, 0);
    }

    public async Task<bool> TryChargeChallengeGuessAsync(TwoFactorAuth configuration, Guid challengeId, int maxGuesses)
    {
        var charged = await context.TwoFactorAuths
            .Where(x => x.Id == configuration.Id && x.ChallengeId == challengeId && x.ChallengeAttempts < maxGuesses)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.ChallengeAttempts, x => x.ChallengeAttempts + 1)) == 1;

        if (charged)
        {
            Accept(configuration, x => x.ChallengeAttempts, configuration.ChallengeAttempts + 1);
        }

        return charged;
    }

    public async Task ExpireChallengeIfExhaustedAsync(TwoFactorAuth configuration, Guid challengeId, int maxGuesses)
    {
        var expired = await context.TwoFactorAuths
            .Where(x => x.Id == configuration.Id && x.ChallengeId == challengeId && x.ChallengeAttempts >= maxGuesses)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.ChallengeId, (Guid?)null)) == 1;

        if (expired)
        {
            Accept(configuration, x => x.ChallengeId, null);
        }
    }

    public async Task<bool> TryRedeemChallengeAsync(TwoFactorAuth configuration, Guid challengeId)
    {
        var redeemed = await context.TwoFactorAuths
            .Where(x => x.Id == configuration.Id && x.ChallengeId == challengeId)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.ChallengeId, (Guid?)null)) == 1;

        if (redeemed)
        {
            Accept(configuration, x => x.ChallengeId, null);
        }

        return redeemed;
    }

    public async Task<bool> TryChargeEmailCodeReissueAsync(TwoFactorAuth configuration, Guid challengeId, int maxReissues)
    {
        var charged = await context.TwoFactorAuths
            .Where(x => x.Id == configuration.Id && x.ChallengeId == challengeId && x.EmailCodeReissueCount < maxReissues)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.EmailCodeReissueCount, x => x.EmailCodeReissueCount + 1)) == 1;

        if (charged)
        {
            Accept(configuration, x => x.EmailCodeReissueCount, configuration.EmailCodeReissueCount + 1);
        }

        return charged;
    }

    public async Task<bool> TryAdvanceTotpStepAsync(TwoFactorAuth configuration, long timeStep, DateTime now)
    {
        var advanced = await context.TwoFactorAuths
            .Where(x => x.Id == configuration.Id &&
                        (x.LastTotpTimeStepUsed == null || x.LastTotpTimeStepUsed < timeStep))
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.LastTotpTimeStepUsed, timeStep)
                .SetProperty(x => x.UpdatedAt, now)) == 1;

        if (advanced)
        {
            Accept(configuration, x => x.LastTotpTimeStepUsed, timeStep);
            Accept(configuration, x => x.UpdatedAt, now);
        }

        return advanced;
    }

    public async Task<bool> TryChargeEmailCodeGuessAsync(TwoFactorEmailCode code, int maxGuesses, DateTime now)
    {
        var charged = await context.TwoFactorEmailCodes
            .Where(x => x.Id == code.Id && !x.Used && x.ExpiresAt > now && x.FailedAttempts < maxGuesses)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.FailedAttempts, x => x.FailedAttempts + 1)) == 1;

        if (charged)
        {
            Accept(code, x => x.FailedAttempts, code.FailedAttempts + 1);
        }

        return charged;
    }

    public async Task RetireEmailCodeIfExhaustedAsync(TwoFactorEmailCode code, int maxGuesses)
    {
        var retired = await context.TwoFactorEmailCodes
            .Where(x => x.Id == code.Id && x.FailedAttempts >= maxGuesses)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Used, true)) == 1;

        if (retired)
        {
            Accept(code, x => x.Used, true);
        }
    }

    public async Task<bool> TryConsumeEmailCodeAsync(TwoFactorEmailCode code)
    {
        var consumed = await context.TwoFactorEmailCodes
            .Where(x => x.Id == code.Id && !x.Used)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Used, true)) == 1;

        if (consumed)
        {
            Accept(code, x => x.Used, true);
        }

        return consumed;
    }

    public async Task<bool> TryConsumeRecoveryCodeAsync(TwoFactorRecoveryCode code, DateTime usedAt)
    {
        var consumed = await context.TwoFactorRecoveryCodes
            .Where(x => x.Id == code.Id && !x.Used)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.Used, true)
                .SetProperty(x => x.UsedAt, usedAt)) == 1;

        if (consumed)
        {
            Accept(code, x => x.Used, true);
            Accept(code, x => x.UsedAt, usedAt);
        }

        return consumed;
    }

    public async Task<bool> TryConsumePasswordResetTokenAsync(PasswordResetToken token, DateTime now)
    {
        var consumed = await context.PasswordResetTokens
            .Where(x => x.Id == token.Id && !x.Used && x.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Used, true)) == 1;

        if (consumed)
        {
            Accept(token, x => x.Used, true);
        }

        return consumed;
    }

    public async Task<bool> TryConsumeEmailVerificationTokenAsync(EmailVerificationToken token, DateTime now)
    {
        var consumed = await context.EmailVerificationTokens
            .Where(x => x.Id == token.Id && !x.Used && x.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Used, true)) == 1;

        if (consumed)
        {
            Accept(token, x => x.Used, true);
        }

        return consumed;
    }

    public async Task<bool> TrySetPasswordAsync(Person person, byte[] passwordHash, byte[] salt, DateTime now)
    {
        var written = await context.Persons
            .Where(RedeemableAsRead(person))
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.PasswordHash, passwordHash)
                .SetProperty(x => x.Salt, salt)
                .SetProperty(x => x.UpdatedAt, now)) == 1;

        if (written)
        {
            Accept(person, x => x.PasswordHash, passwordHash);
            Accept(person, x => x.Salt, salt);
            Accept(person, x => x.UpdatedAt, now);
        }

        return written;
    }

    public async Task<bool> TryMarkEmailVerifiedAsync(Person person, DateTime now)
    {
        var written = await context.Persons
            .Where(RedeemableAsRead(person))
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.EmailVerified, true)
                .SetProperty(x => x.UpdatedAt, now)) == 1;

        if (written)
        {
            Accept(person, x => x.EmailVerified, true);
            Accept(person, x => x.UpdatedAt, now);
        }

        return written;
    }

    public async Task<bool> TryStampCredentialUseAsync(ApplicationCredential credential, DateTime now)
    {
        var stamped = await context.ApplicationCredentials
            .Where(x => x.Id == credential.Id)
            .Where(ApplicationCredential.LiveAt(now))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.LastUsedAt, now)) == 1;

        if (stamped)
        {
            Accept(credential, x => x.LastUsedAt, now);
        }

        return stamped;
    }

    public async Task<bool> TryRevokeCredentialAsync(ApplicationCredential credential, DateTime revokedAt)
    {
        var revoked = await context.ApplicationCredentials
            .Where(x => x.Id == credential.Id && x.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAt, revokedAt)) == 1;

        if (revoked)
        {
            Accept(credential, x => x.RevokedAt, revokedAt);
        }

        return revoked;
    }

    public async Task RotateSecurityStampAsync(Person person)
    {
        var stamp = Guid.NewGuid();

        await context.Persons
            .Where(x => x.Id == person.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.SecurityStamp, stamp));

        Accept(person, x => x.SecurityStamp, stamp);
    }

    /// <summary>
    ///     The row <paramref name="person" /> was read from, if it still holds the address it was
    ///     read with and is not deleted, restricted or anonymised: the condition a single-use token
    ///     is redeemed under (FR-PR-08, FR-EV-08). The address is captured now, so the comparison is
    ///     against the value the caller read, not whatever the row holds by the time it runs.
    /// </summary>
    private static Expression<Func<Person, bool>> RedeemableAsRead(Person person)
    {
        var id = person.Id;
        var email = person.Email;

        return x => x.Id == id && x.Email == email && !x.IsDeleted &&
                    x.ProcessingRestrictedAt == null && x.AnonymisedAt == null;
    }

    /// <summary>
    ///     Writes the person's row without changing it, after a factor reservation was refused.
    /// </summary>
    /// <remarks>
    ///     A refused reservation's <c>UPDATE</c> matches no row, so PostgreSQL assigns it no transaction
    ///     and flushes no commit, and it answers measurably sooner than an admitted one. That would
    ///     tell a caller the second factor is locked, or re-authentication paused, without a word of
    ///     the answer differing (FR-AU-12, FR-AU-13). Setting a column to itself writes a new row
    ///     version and commits like the admitted write does; what remains is one more round trip.
    /// </remarks>
    private Task TouchAsync(Person person) =>
        context.Persons
            .Where(x => x.Id == person.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.FailedSecondFactorAttempts, x => x.FailedSecondFactorAttempts));

    /// <summary>
    ///     The value <see cref="TryReserveSecondFactorAttemptAsync" /> writes to the lock: unchanged,
    ///     unless this reservation reaches <paramref name="maxAttempts" />, and then the entry of
    ///     <paramref name="lockedOutUntil" /> the stored lockout count selects — a <c>CASE</c> over the
    ///     count, with the last entry for every count from its own on.
    /// </summary>
    /// <remarks>
    ///     Built as an expression tree because the number of entries is the caller's. Each end is read
    ///     from a box rather than inlined, so EF Core sends it as a parameter, and the statement — and
    ///     its cached plan — is the same on every call.
    /// </remarks>
    private static Expression<Func<Person, DateTime?>> SecondFactorLockEnd(
        int maxAttempts, IReadOnlyList<DateTime> lockedOutUntil)
    {
        var row = Expression.Parameter(typeof(Person), "x");
        var lockouts = Expression.Property(row, nameof(Person.SecondFactorLockouts));

        Expression end = Boxed(lockedOutUntil[^1]);

        for (var lockout = lockedOutUntil.Count - 2; lockout >= 0; lockout--)
        {
            end = Expression.Condition(
                Expression.Equal(lockouts, Expression.Constant(lockout)), Boxed(lockedOutUntil[lockout]), end);
        }

        var reachesLimit = Expression.GreaterThanOrEqual(
            Expression.Add(Expression.Property(row, nameof(Person.FailedSecondFactorAttempts)), Expression.Constant(1)),
            Boxed(maxAttempts));

        return Expression.Lambda<Func<Person, DateTime?>>(
            Expression.Condition(reachesLimit, end, Expression.Property(row, nameof(Person.SecondFactorLockedOutUntil))),
            row);
    }

    /// <summary>The lock end <see cref="SecondFactorLockEnd" /> selects, for the instance.</summary>
    private static DateTime LockEndAfter(int lockouts, IReadOnlyList<DateTime> lockedOutUntil) =>
        lockouts >= 0 && lockouts < lockedOutUntil.Count - 1 ? lockedOutUntil[lockouts] : lockedOutUntil[^1];

    private static MemberExpression Boxed(DateTime value) =>
        Expression.Field(Expression.Constant(new StrongBox<DateTime?>(value)), nameof(StrongBox<DateTime?>.Value));

    private static MemberExpression Boxed(int value) =>
        Expression.Field(Expression.Constant(new StrongBox<int>(value)), nameof(StrongBox<int>.Value));

    /// <summary>
    ///     Brings the caller's instance in line with a write the change tracker did not see: the new
    ///     value becomes both current and original, so the property is not saved again later.
    /// </summary>
    private void Accept<T, TProperty>(T entity, Expression<Func<T, TProperty>> property, TProperty value)
        where T : class
    {
        var entry = context.Entry(entity);
        var member = entry.Property(property);

        member.CurrentValue = value;

        if (entry.State is EntityState.Unchanged or EntityState.Modified)
        {
            member.OriginalValue = value;
            member.IsModified = false;
        }
    }
}
