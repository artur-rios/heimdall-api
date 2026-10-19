using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
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

    /// <summary>
    ///     Called with the person as <see cref="TrySetPasswordAsync" /> is about to write their new
    ///     password, so a test can see what the row held at that moment.
    /// </summary>
    public Action<Person>? OnPasswordWrite { get; init; }

    /// <summary>
    ///     Called with the person before any conditional write to their row evaluates its condition —
    ///     <see cref="TrySetPasswordAsync" />, <see cref="TryMarkEmailVerifiedAsync" /> and the
    ///     lifecycle writes (#237) — so a test can land a change on the row between the handler's own
    ///     checks and the write.
    /// </summary>
    public Action<Person>? BeforeConditionalWrite { get; init; }

    /// <summary>
    ///     The Google ID token keys recorded as used (FR-GO-21), or <c>null</c> — as on
    ///     <see cref="Instance" />, which every test shares — to accept every use without keeping one,
    ///     so tests that present the same placeholder token do not refuse each other.
    /// </summary>
    public ISet<string>? GoogleIdTokenUses { get; init; }

    /// <summary>
    ///     The persons the test's fake repository holds, for a write whose condition reaches past the
    ///     row it writes: <see cref="TryRemoveScopeOwnerAsync" />'s live co-owner. Required by that
    ///     write and read by no other.
    /// </summary>
    public IQueryable<Person>? Persons { get; init; }

    /// <summary>How many pending-registration guesses have been charged through this instance.</summary>
    public int PendingGuessCharges { get; private set; }

    public Task<bool> TryChargePendingRegistrationGuessAsync(PendingRegistration pending, int maxGuesses)
    {
        if (pending.FailedAttempts >= maxGuesses)
        {
            return Task.FromResult(false);
        }

        pending.FailedAttempts++;
        PendingGuessCharges++;

        return Task.FromResult(true);
    }

    public Task<bool> TryReserveLoginAttemptAsync(
        Person person, int maxAttempts, DateTime now, DateTime lockedOutUntil)
    {
        var stuck = person.FailedLoginAttempts >= maxAttempts && person.LockedOutUntil is null;

        if (!stuck && (person.FailedLoginAttempts >= maxAttempts || person.LockedOutUntil > now))
        {
            return Task.FromResult(false);
        }

        if (person.FailedLoginAttempts + 1 >= maxAttempts)
        {
            person.LockedOutUntil = lockedOutUntil;
            person.FailedLoginAttempts = 0;
        }
        else
        {
            person.FailedLoginAttempts++;
        }

        return Task.FromResult(true);
    }

    public Task ClearLoginAttemptsAsync(Person person)
    {
        person.FailedLoginAttempts = 0;
        person.LockedOutUntil = null;

        return Task.CompletedTask;
    }

    public Task<bool> TryReserveSecondFactorAttemptAsync(
        Person person, int maxAttempts, DateTime now, IReadOnlyList<DateTime> lockedOutUntil)
    {
        var stuck = person.FailedSecondFactorAttempts >= maxAttempts &&
                    (person.SecondFactorLockedOutUntil is null || person.SecondFactorLockedOutUntil <= now);

        if (!stuck && (person.FailedSecondFactorAttempts >= maxAttempts || person.SecondFactorLockedOutUntil > now))
        {
            return Task.FromResult(false);
        }

        if (person.FailedSecondFactorAttempts + 1 >= maxAttempts)
        {
            person.SecondFactorLockedOutUntil = lockedOutUntil[Math.Min(person.SecondFactorLockouts, lockedOutUntil.Count - 1)];
            person.SecondFactorLockouts++;
            person.FailedSecondFactorAttempts = 0;
        }
        else
        {
            person.FailedSecondFactorAttempts++;
        }

        return Task.FromResult(true);
    }

    public Task ClearSecondFactorAttemptsAsync(Person person)
    {
        person.FailedSecondFactorAttempts = 0;
        person.SecondFactorLockedOutUntil = null;
        person.SecondFactorLockouts = 0;

        return Task.CompletedTask;
    }

    public Task<bool> TryReserveReauthenticationAsync(
        Person person, int maxFailures, DateTime now, DateTime windowStartedBy, DateTime pausedUntil)
    {
        if (person.ReauthenticationPausedUntil > now)
        {
            return Task.FromResult(false);
        }

        var windowOpen = person.ReauthenticationWindowStartedAt > windowStartedBy;
        var failures = windowOpen ? person.FailedReauthenticationAttempts + 1 : 1;

        if (failures >= maxFailures)
        {
            person.ReauthenticationPausedUntil = pausedUntil;
            person.FailedReauthenticationAttempts = 0;
            person.ReauthenticationWindowStartedAt = null;
        }
        else
        {
            person.FailedReauthenticationAttempts = failures;
            person.ReauthenticationWindowStartedAt = windowOpen ? person.ReauthenticationWindowStartedAt : now;
        }

        return Task.FromResult(true);
    }

    public Task ClearReauthenticationAttemptsAsync(Person person)
    {
        person.FailedReauthenticationAttempts = 0;
        person.ReauthenticationWindowStartedAt = null;
        person.ReauthenticationPausedUntil = null;

        return Task.CompletedTask;
    }

    public Task<AccountEmailReservation> TryReserveAccountEmailAsync(
        Person person, int maxPerWindow, DateTime now, DateTime lastSentBefore, DateTime windowEndsAt)
    {
        var windowOpen = person.AccountEmailWindowEndsAt > now;

        if (windowOpen && person.AccountEmailsSent >= maxPerWindow)
        {
            return Task.FromResult(AccountEmailReservation.DailyLimitReached);
        }

        if (person.AccountEmailLastSentAt > lastSentBefore)
        {
            return Task.FromResult(AccountEmailReservation.CoolingDown);
        }

        person.AccountEmailsSent = windowOpen ? person.AccountEmailsSent + 1 : 1;
        person.AccountEmailWindowEndsAt = windowOpen ? person.AccountEmailWindowEndsAt : windowEndsAt;
        person.AccountEmailLastSentAt = now;

        return Task.FromResult(AccountEmailReservation.Reserved);
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

    public Task<bool> TrySetPasswordAsync(Person person, byte[] passwordHash, byte[] salt, DateTime now)
    {
        BeforeConditionalWrite?.Invoke(person);

        if (!RedeemableAsRead(person))
        {
            return Task.FromResult(false);
        }

        OnPasswordWrite?.Invoke(person);

        person.PasswordHash = passwordHash;
        person.Salt = salt;
        person.SecurityStamp = Guid.NewGuid();
        person.UpdatedAt = now;

        return Task.FromResult(true);
    }

    public Task<bool> TryMarkEmailVerifiedAsync(Person person, DateTime now)
    {
        BeforeConditionalWrite?.Invoke(person);

        if (!RedeemableAsRead(person))
        {
            return Task.FromResult(false);
        }

        person.EmailVerified = true;
        person.UpdatedAt = now;

        return Task.FromResult(true);
    }

    /// <summary>
    ///     The state half of the condition. The instance is the row here, so the address it was read
    ///     with is always the one it holds; only the functional suite can change it in between.
    /// </summary>
    private static bool RedeemableAsRead(Person person) =>
        !person.IsDeleted && person.ProcessingRestrictedAt is null && person.AnonymisedAt is null;

    public Task<bool> TryStampCredentialUseAsync(ApplicationCredential credential, DateTime now)
    {
        if (!credential.IsLiveAt(now))
        {
            return Task.FromResult(false);
        }

        credential.LastUsedAt = now;

        return Task.FromResult(true);
    }

    public Task<bool> TryRevokeCredentialAsync(ApplicationCredential credential, DateTime revokedAt)
    {
        if (credential.RevokedAt is not null)
        {
            return Task.FromResult(false);
        }

        credential.RevokedAt = revokedAt;

        return Task.FromResult(true);
    }

    public Task RotateSecurityStampAsync(Person person)
    {
        person.SecurityStamp = Guid.NewGuid();

        return Task.CompletedTask;
    }

    public async Task<AccountChangeOutcome> TryChangeEmailAndRoleAsync(
        Person person, string email, long roleId, DateTime now, Func<Task<bool>> saveAlongside)
    {
        // What the caller read, compared with the row once the hook has had its say.
        var readEmail = person.Email;
        var readRoleId = person.RoleId;

        BeforeConditionalWrite?.Invoke(person);

        if (person.Email != readEmail || person.RoleId != readRoleId ||
            person.IsDeleted || person.AnonymisedAt is not null)
        {
            return AccountChangeOutcome.Stale;
        }

        if (!string.Equals(email, person.Email, StringComparison.Ordinal))
        {
            person.Email = email;
            person.EmailVerified = false;
        }

        if (roleId != person.RoleId)
        {
            person.RoleId = roleId;
            person.ScopeId = null;
        }

        person.UpdatedAt = now;

        return await saveAlongside() ? AccountChangeOutcome.Changed : AccountChangeOutcome.SaveFailed;
    }

    public Task<bool> TryMarkDeletedAsync(Person person, DeletionKinds kind, DateTime at)
    {
        BeforeConditionalWrite?.Invoke(person);

        if (person.IsDeleted)
        {
            return Task.FromResult(false);
        }

        person.IsDeleted = true;
        person.DeletedAt = at;
        person.DeletionKind = (int)kind;
        person.UpdatedAt = at;

        return Task.FromResult(true);
    }

    public Task<bool> TryRecordErasureRequestAsync(
        Person person, DateTime requestedAt, DateTime dueAt, string? blockedReason)
    {
        BeforeConditionalWrite?.Invoke(person);

        if (person.ErasureRequestedAt is not null || person.IsDeleted || person.AnonymisedAt is not null)
        {
            return Task.FromResult(false);
        }

        person.ErasureRequestedAt = requestedAt;
        person.ErasureDueAt = dueAt;
        person.ProcessingRestrictedAt = null;
        person.RestrictionGround = null;
        person.RestrictionLiftNotifiedAt = null;
        person.UpdatedAt = requestedAt;

        if (blockedReason is null)
        {
            person.IsDeleted = true;
            person.DeletedAt = requestedAt;
            person.DeletionKind = (int)DeletionKinds.SubjectRequested;
        }
        else
        {
            person.ErasureBlockedReason = blockedReason;
        }

        return Task.FromResult(true);
    }

    public Task<bool> TrySuspendBlockedErasureAsync(Person person, DateTime now)
    {
        BeforeConditionalWrite?.Invoke(person);

        if (person.IsDeleted || person.ErasureRequestedAt is null || person.ErasureBlockedReason is null ||
            person.ProcessingRestrictedAt is not null)
        {
            return Task.FromResult(false);
        }

        person.IsDeleted = true;
        person.DeletedAt = person.ErasureRequestedAt;
        person.DeletionKind = (int)DeletionKinds.SubjectRequested;
        person.ErasureBlockedReason = null;
        person.UpdatedAt = now;

        return Task.FromResult(true);
    }

    public Task<bool> TryRestrictProcessingAsync(Person person, int ground, DateTime at)
    {
        BeforeConditionalWrite?.Invoke(person);

        if (person.ProcessingRestrictedAt is not null || person.AnonymisedAt is not null)
        {
            return Task.FromResult(false);
        }

        person.ProcessingRestrictedAt = at;
        person.RestrictionGround = ground;
        person.RestrictionLiftNotifiedAt = null;
        person.UpdatedAt = at;

        return Task.FromResult(true);
    }

    public Task<bool> TryLiftRestrictionAsync(Person person, DateTime? notifiedAt, DateTime now)
    {
        var restrictedAt = person.ProcessingRestrictedAt;

        BeforeConditionalWrite?.Invoke(person);

        if (restrictedAt is null || person.ProcessingRestrictedAt != restrictedAt)
        {
            return Task.FromResult(false);
        }

        person.ProcessingRestrictedAt = null;
        person.RestrictionGround = null;
        person.RestrictionLiftNotifiedAt = notifiedAt;
        person.UpdatedAt = now;

        return Task.FromResult(true);
    }

    public Task<bool> TryAnonymiseAsync(Person person, string name, string email, DateTime at)
    {
        BeforeConditionalWrite?.Invoke(person);

        if (!person.IsDeleted || person.AnonymisedAt is not null || person.ProcessingRestrictedAt is not null)
        {
            return Task.FromResult(false);
        }

        Anonymise(person, name, email, at);

        return Task.FromResult(true);
    }

    public Task<bool> TryReapplyErasureAsync(Person person, string name, string email, DateTime at)
    {
        BeforeConditionalWrite?.Invoke(person);

        if (person.AnonymisedAt is not null)
        {
            return Task.FromResult(false);
        }

        person.IsDeleted = true;
        person.DeletedAt ??= at;
        person.DeletionKind = (int)DeletionKinds.SubjectRequested;
        Anonymise(person, name, email, at);

        return Task.FromResult(true);
    }

    /// <summary>The columns both anonymisation writes overwrite, as <c>AtomicWrites</c> sets them.</summary>
    private static void Anonymise(Person person, string name, string email, DateTime at)
    {
        person.Name = name;
        person.Email = email;
        person.PasswordHash = [];
        person.Salt = [];
        person.EmailVerified = false;
        person.FailedLoginAttempts = 0;
        person.LockedOutUntil = null;
        person.FailedSecondFactorAttempts = 0;
        person.SecondFactorLockedOutUntil = null;
        person.SecondFactorLockouts = 0;
        person.FailedReauthenticationAttempts = 0;
        person.ReauthenticationWindowStartedAt = null;
        person.ReauthenticationPausedUntil = null;
        person.AccountEmailsSent = 0;
        person.AccountEmailWindowEndsAt = null;
        person.AccountEmailLastSentAt = null;
        person.ErasureBlockedReason = null;
        person.AnonymisedAt = at;
        person.UpdatedAt = at;
    }

    public Task<bool> TryAddScopeOwnerAsync(Person person, long scopeId)
    {
        BeforeConditionalWrite?.Invoke(person);

        if (person.IsDeleted ||
            person.RoleId is not ((long)Roles.ScopeAdmin or (long)Roles.SystemAdmin) ||
            person.ScopeOwnerships.Any(x => x.ScopeId == scopeId))
        {
            return Task.FromResult(false);
        }

        person.ScopeOwnerships.Add(new ScopeOwner { ScopeId = scopeId, PersonId = person.Id });

        return Task.FromResult(true);
    }

    /// <remarks>
    ///     The co-owner condition is evaluated here, at the write, over <see cref="Persons" />, as the
    ///     statement's <c>EXISTS</c> is: a co-owner deleted since the handler's own check is seen.
    /// </remarks>
    public Task<bool> TryRemoveScopeOwnerAsync(Person person, long scopeId)
    {
        var persons = Persons ?? throw new InvalidOperationException(
            $"{nameof(TryRemoveScopeOwnerAsync)} needs {nameof(Persons)} to find a live co-owner.");

        BeforeConditionalWrite?.Invoke(person);

        var ownership = person.ScopeOwnerships.FirstOrDefault(x => x.ScopeId == scopeId);

        var liveCoOwner = persons.Any(other =>
            other.Id != person.Id && !other.IsDeleted && other.ScopeOwnerships.Any(x => x.ScopeId == scopeId));

        return Task.FromResult(ownership is not null && liveCoOwner && person.ScopeOwnerships.Remove(ownership));
    }

    public Task<bool> TryRecordGoogleIdTokenUseAsync(string tokenHash, DateTime expiresAt)
    {
        if (GoogleIdTokenUses is null)
        {
            return Task.FromResult(true);
        }

        lock (GoogleIdTokenUses)
        {
            return Task.FromResult(GoogleIdTokenUses.Add(tokenHash));
        }
    }
}
