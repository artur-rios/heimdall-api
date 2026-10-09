using System.Linq.Expressions;
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
    public async Task<bool> TryReserveLoginAttemptAsync(Person person, int maxAttempts, DateTime now)
    {
        var reserved = await context.Persons
            .Where(x => x.Id == person.Id && x.FailedLoginAttempts < maxAttempts &&
                        (x.LockedOutUntil == null || x.LockedOutUntil <= now))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.FailedLoginAttempts, x => x.FailedLoginAttempts + 1)) == 1;

        if (reserved)
        {
            Accept(person, x => x.FailedLoginAttempts, person.FailedLoginAttempts + 1);
        }

        return reserved;
    }

    public async Task LockOutIfExhaustedAsync(Person person, int maxAttempts, DateTime lockedOutUntil)
    {
        var locked = await context.Persons
            .Where(x => x.Id == person.Id && x.FailedLoginAttempts >= maxAttempts)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.LockedOutUntil, lockedOutUntil)
                .SetProperty(x => x.FailedLoginAttempts, 0)) == 1;

        if (locked)
        {
            Accept(person, x => x.LockedOutUntil, lockedOutUntil);
            Accept(person, x => x.FailedLoginAttempts, 0);
        }
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
