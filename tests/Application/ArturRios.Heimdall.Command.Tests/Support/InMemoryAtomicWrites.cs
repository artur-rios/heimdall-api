using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;

namespace ArturRios.Heimdall.Command.Tests.Support;

/// <summary>
///     <see cref="IAtomicWrites" /> over the instances the fake repositories hold — which are the same
///     instances a handler reads — applying each method's condition and write in memory, as the
///     database applies them to the row. What a unit test cannot show is two requests racing; the
///     functional concurrency tests run these writes against PostgreSQL for that.
/// </summary>
public sealed class InMemoryAtomicWrites : IAtomicWrites
{
    public static readonly InMemoryAtomicWrites Instance = new();

    public Task<bool> TryReserveLoginAttemptAsync(Person person, int maxAttempts, DateTime now)
    {
        if (person.FailedLoginAttempts >= maxAttempts || person.LockedOutUntil > now)
        {
            return Task.FromResult(false);
        }

        person.FailedLoginAttempts++;

        return Task.FromResult(true);
    }

    public Task LockOutIfExhaustedAsync(Person person, int maxAttempts, DateTime lockedOutUntil)
    {
        if (person.FailedLoginAttempts >= maxAttempts)
        {
            person.LockedOutUntil = lockedOutUntil;
            person.FailedLoginAttempts = 0;
        }

        return Task.CompletedTask;
    }

    public Task ClearLoginAttemptsAsync(Person person)
    {
        person.FailedLoginAttempts = 0;
        person.LockedOutUntil = null;

        return Task.CompletedTask;
    }

    public Task StartChallengeAsync(TwoFactorAuth configuration, Guid challengeId)
    {
        configuration.ChallengeId = challengeId;
        configuration.EmailCodeReissueCount = 0;
        configuration.ChallengeAttempts = 0;

        return Task.CompletedTask;
    }

    public Task<bool> TryChargeChallengeGuessAsync(TwoFactorAuth configuration, Guid challengeId, int maxGuesses)
    {
        if (configuration.ChallengeId != challengeId || configuration.ChallengeAttempts >= maxGuesses)
        {
            return Task.FromResult(false);
        }

        configuration.ChallengeAttempts++;

        return Task.FromResult(true);
    }

    public Task ExpireChallengeIfExhaustedAsync(TwoFactorAuth configuration, Guid challengeId, int maxGuesses)
    {
        if (configuration.ChallengeId == challengeId && configuration.ChallengeAttempts >= maxGuesses)
        {
            configuration.ChallengeId = null;
        }

        return Task.CompletedTask;
    }

    public Task<bool> TryRedeemChallengeAsync(TwoFactorAuth configuration, Guid challengeId)
    {
        if (configuration.ChallengeId != challengeId)
        {
            return Task.FromResult(false);
        }

        configuration.ChallengeId = null;

        return Task.FromResult(true);
    }

    public Task<bool> TryChargeEmailCodeReissueAsync(TwoFactorAuth configuration, Guid challengeId, int maxReissues)
    {
        if (configuration.ChallengeId != challengeId || configuration.EmailCodeReissueCount >= maxReissues)
        {
            return Task.FromResult(false);
        }

        configuration.EmailCodeReissueCount++;

        return Task.FromResult(true);
    }

    public Task<bool> TryAdvanceTotpStepAsync(TwoFactorAuth configuration, long timeStep, DateTime now)
    {
        if (configuration.LastTotpTimeStepUsed >= timeStep)
        {
            return Task.FromResult(false);
        }

        configuration.LastTotpTimeStepUsed = timeStep;
        configuration.UpdatedAt = now;

        return Task.FromResult(true);
    }

    public Task<bool> TryChargeEmailCodeGuessAsync(TwoFactorEmailCode code, int maxGuesses, DateTime now)
    {
        if (code.Used || code.ExpiresAt <= now || code.FailedAttempts >= maxGuesses)
        {
            return Task.FromResult(false);
        }

        code.FailedAttempts++;

        return Task.FromResult(true);
    }

    public Task RetireEmailCodeIfExhaustedAsync(TwoFactorEmailCode code, int maxGuesses)
    {
        if (code.FailedAttempts >= maxGuesses)
        {
            code.Used = true;
        }

        return Task.CompletedTask;
    }

    public Task<bool> TryConsumeEmailCodeAsync(TwoFactorEmailCode code)
    {
        if (code.Used)
        {
            return Task.FromResult(false);
        }

        code.Used = true;

        return Task.FromResult(true);
    }

    public Task<bool> TryConsumeRecoveryCodeAsync(TwoFactorRecoveryCode code, DateTime usedAt)
    {
        if (code.Used)
        {
            return Task.FromResult(false);
        }

        code.Used = true;
        code.UsedAt = usedAt;

        return Task.FromResult(true);
    }

    public Task<bool> TryConsumePasswordResetTokenAsync(PasswordResetToken token, DateTime now)
    {
        if (token.Used || token.ExpiresAt <= now)
        {
            return Task.FromResult(false);
        }

        token.Used = true;

        return Task.FromResult(true);
    }

    public Task<bool> TryConsumeEmailVerificationTokenAsync(EmailVerificationToken token, DateTime now)
    {
        if (token.Used || token.ExpiresAt <= now)
        {
            return Task.FromResult(false);
        }

        token.Used = true;

        return Task.FromResult(true);
    }
}
