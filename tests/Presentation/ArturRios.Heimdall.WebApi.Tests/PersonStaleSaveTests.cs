using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Data.Persistence;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     Issue #237, against the real database: a person's lifecycle columns — role and scope, address and
///     its verification, login lockout, deletion, erasure request, restriction and anonymisation — are
///     written only by the targeted writes in <see cref="AtomicWrites" />, and a whole-row save of a copy
///     read before one of those writes leaves it in place.
/// </summary>
/// <remarks>
///     Each test reads a copy of the person on one context, makes the transition on a second, then
///     saves the first copy the way the repositories do — <c>Update</c> and <c>SaveChanges</c>, every
///     column the mapping lets it write — with a changed name, so the save is known to have happened.
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class PersonStaleSaveTests(PostgresFixture db)
{
    private const string SavedLater = "Saved later";
    private const string RealName = "Ada Lovelace";

    private async Task<Person> SeedPersonAsync(Action<Person>? configure = null)
    {
        await using var context = db.CreateContext();

        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = RealName,
            Email = $"stale-{Guid.NewGuid():N}@test.local",
            PasswordHash = [1, 2, 3],
            Salt = [4, 5, 6],
            RoleId = (long)Roles.ScopeAdmin,
            EmailVerified = true
        };

        configure?.Invoke(person);

        context.Persons.Add(person);
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();

        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        return scope;
    }

    /// <summary>
    ///     Reads a copy of the person, runs <paramref name="transition" /> on a second context, then
    ///     saves the copy whole, renamed, and returns the row as stored afterwards.
    /// </summary>
    private async Task<Person> StaleSaveAcrossAsync(long personId, Func<AtomicWrites, Person, AppDbContext, Task> transition)
    {
        await using var stale = db.CreateContext();
        var staleCopy = await stale.Persons.SingleAsync(x => x.Id == personId);

        await using (var writing = db.CreateContext())
        {
            var current = await writing.Persons.SingleAsync(x => x.Id == personId);
            await transition(new AtomicWrites(writing), current, writing);
        }

        staleCopy.Name = SavedLater;
        stale.Persons.Update(staleCopy);
        await stale.SaveChangesAsync();

        return await StoredAsync(personId);
    }

    private Task<Person> StaleSaveAcrossAsync(long personId, Func<AtomicWrites, Person, Task> transition) =>
        StaleSaveAcrossAsync(personId, (writes, person, _) => transition(writes, person));

    private async Task<Person> StoredAsync(long personId)
    {
        await using var check = db.CreateContext();

        return await check.Persons
            .Include(x => x.ScopeMembership)
            .Include(x => x.ScopeOwnerships)
            .SingleAsync(x => x.Id == personId);
    }

    private static DateTime Now() => DateTime.UtcNow;

    [FunctionalFact]
    public async Task GivenACopyReadBeforeARestriction_WhenSavedAsAWholeRow_ThenTheRestrictionStands()
    {
        // Given
        var person = await SeedPersonAsync();
        var at = Now();

        // When
        var stored = await StaleSaveAcrossAsync(person.Id, (writes, current) =>
            writes.TryRestrictProcessingAsync(current, (int)RestrictionGrounds.AccuracyContested, at));

        // Then
        Assert.NotNull(stored.ProcessingRestrictedAt);
        Assert.Equal((int)RestrictionGrounds.AccuracyContested, stored.RestrictionGround);
        Assert.Equal(SavedLater, stored.Name);
    }

    [FunctionalFact]
    public async Task GivenACopyReadBeforeALift_WhenSavedAsAWholeRow_ThenTheLiftStands()
    {
        // Given
        var person = await SeedPersonAsync(p =>
        {
            p.ProcessingRestrictedAt = Now().AddDays(-1);
            p.RestrictionGround = (int)RestrictionGrounds.ObjectionPending;
        });
        var notifiedAt = Now();

        // When
        var stored = await StaleSaveAcrossAsync(person.Id, (writes, current) =>
            writes.TryLiftRestrictionAsync(current, notifiedAt, notifiedAt));

        // Then
        Assert.Null(stored.ProcessingRestrictedAt);
        Assert.Null(stored.RestrictionGround);
        Assert.NotNull(stored.RestrictionLiftNotifiedAt);
        Assert.Equal(SavedLater, stored.Name);
    }

    [FunctionalFact]
    public async Task GivenACopyReadBeforeADeletion_WhenSavedAsAWholeRow_ThenTheDeletionStands()
    {
        // Given
        var person = await SeedPersonAsync();

        // When
        var stored = await StaleSaveAcrossAsync(person.Id, (writes, current) =>
            writes.TryMarkDeletedAsync(current, DeletionKinds.Administrative, Now()));

        // Then
        Assert.True(stored.IsDeleted);
        Assert.NotNull(stored.DeletedAt);
        Assert.Equal((int)DeletionKinds.Administrative, stored.DeletionKind);
    }

    [FunctionalFact]
    public async Task GivenACopyReadBeforeAnErasureRequest_WhenSavedAsAWholeRow_ThenTheRequestAndSuspensionStand()
    {
        // Given a restricted person, whose request also lifts the restriction
        var person = await SeedPersonAsync(p =>
        {
            p.ProcessingRestrictedAt = Now().AddDays(-1);
            p.RestrictionGround = (int)RestrictionGrounds.AccuracyContested;
        });
        var requestedAt = Now();

        // When
        var stored = await StaleSaveAcrossAsync(person.Id, (writes, current) =>
            writes.TryRecordErasureRequestAsync(current, requestedAt, requestedAt.AddDays(30), null));

        // Then
        Assert.NotNull(stored.ErasureRequestedAt);
        Assert.NotNull(stored.ErasureDueAt);
        Assert.True(stored.IsDeleted);
        Assert.Equal((int)DeletionKinds.SubjectRequested, stored.DeletionKind);
        Assert.Null(stored.ProcessingRestrictedAt);
    }

    [FunctionalFact]
    public async Task GivenACopyReadBeforeABlockedErasureRequest_WhenSavedAsAWholeRow_ThenTheBlockStands()
    {
        // Given
        var person = await SeedPersonAsync();
        var requestedAt = Now();

        // When
        var stored = await StaleSaveAcrossAsync(person.Id, (writes, current) =>
            writes.TryRecordErasureRequestAsync(current, requestedAt, requestedAt.AddDays(30), "blocked"));

        // Then
        Assert.NotNull(stored.ErasureRequestedAt);
        Assert.Equal("blocked", stored.ErasureBlockedReason);
        Assert.False(stored.IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenACopyReadBeforeABlockedErasureClears_WhenSavedAsAWholeRow_ThenTheSuspensionStands()
    {
        // Given
        var requestedAt = Now().AddDays(-2);
        var person = await SeedPersonAsync(p =>
        {
            p.ErasureRequestedAt = requestedAt;
            p.ErasureDueAt = requestedAt.AddDays(30);
            p.ErasureBlockedReason = "blocked";
        });

        // When
        var stored = await StaleSaveAcrossAsync(person.Id, (writes, current) =>
            writes.TrySuspendBlockedErasureAsync(current, Now()));

        // Then the deletion is dated from the request, and the block is gone
        Assert.True(stored.IsDeleted);
        Assert.Equal(requestedAt, stored.DeletedAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Null(stored.ErasureBlockedReason);
    }

    [FunctionalFact]
    public async Task GivenACopyReadBeforeAnonymisation_WhenSavedAsAWholeRow_ThenTheAnonymisationStands()
    {
        // Given a deleted person with a lockout on record
        var person = await SeedPersonAsync(p =>
        {
            p.IsDeleted = true;
            p.DeletedAt = Now().AddDays(-400);
            p.DeletionKind = (int)DeletionKinds.Administrative;
            p.FailedLoginAttempts = 3;
            p.LockedOutUntil = Now().AddMinutes(10);
        });
        var placeholder = $"anonymised-{person.PublicId:N}@anonymised.invalid";

        // When
        var stored = await StaleSaveAcrossAsync(person.Id, (writes, current) =>
            writes.TryAnonymiseAsync(current, "Anonymised", placeholder, Now()));

        // Then every column anonymisation clears stays cleared. The name is plain profile and stays on
        // the whole-row path, so it is not asserted here.
        Assert.NotNull(stored.AnonymisedAt);
        Assert.Equal(placeholder, stored.Email);
        Assert.Empty(stored.PasswordHash);
        Assert.Empty(stored.Salt);
        Assert.False(stored.EmailVerified);
        Assert.Equal(0, stored.FailedLoginAttempts);
        Assert.Null(stored.LockedOutUntil);
    }

    [FunctionalFact]
    public async Task GivenACopyReadBeforeAReappliedErasure_WhenSavedAsAWholeRow_ThenTheErasureStands()
    {
        // Given a live person a restore brought back
        var person = await SeedPersonAsync();
        var placeholder = $"anonymised-{person.PublicId:N}@anonymised.invalid";

        // When
        var stored = await StaleSaveAcrossAsync(person.Id, (writes, current) =>
            writes.TryReapplyErasureAsync(current, "Anonymised", placeholder, Now()));

        // Then
        Assert.True(stored.IsDeleted);
        Assert.Equal((int)DeletionKinds.SubjectRequested, stored.DeletionKind);
        Assert.NotNull(stored.AnonymisedAt);
        Assert.Equal(placeholder, stored.Email);
        Assert.Empty(stored.PasswordHash);
    }

    [FunctionalFact]
    public async Task GivenACopyReadBeforeAnAddressChange_WhenSavedAsAWholeRow_ThenTheNewAddressStands()
    {
        // Given
        var person = await SeedPersonAsync();
        var newEmail = $"changed-{Guid.NewGuid():N}@test.local";

        // When
        var stored = await StaleSaveAcrossAsync(person.Id, async (writes, current, context) =>
            Assert.Equal(AccountChangeOutcome.Changed, await writes.TryChangeEmailAndRoleAsync(
                current, newEmail, current.RoleId, Now(), async () =>
                {
                    await context.SaveChangesAsync();
                    return true;
                })));

        // Then the address and its cleared verification both stand
        Assert.Equal(newEmail, stored.Email);
        Assert.False(stored.EmailVerified);
        Assert.Equal(SavedLater, stored.Name);
    }

    [FunctionalFact]
    public async Task GivenAnAddressChangeOnlyInCase_WhenChanged_ThenTheAddressStaysVerified()
    {
        // Given a verified person
        var person = await SeedPersonAsync(p => p.EmailVerified = true);
        var recased = person.Email.ToUpperInvariant();

        // When only the case of the address changes (#225)
        var stored = await StaleSaveAcrossAsync(person.Id, async (writes, current, context) =>
            Assert.Equal(AccountChangeOutcome.Changed, await writes.TryChangeEmailAndRoleAsync(
                current, recased, current.RoleId, Now(), async () =>
                {
                    await context.SaveChangesAsync();
                    return true;
                })));

        // Then the new casing is stored and the verification stands
        Assert.Equal(recased, stored.Email);
        Assert.True(stored.EmailVerified);
    }

    [FunctionalFact]
    public async Task GivenACopyReadBeforeAPromotion_WhenSavedAsAWholeRow_ThenTheRoleAndOwnershipStand()
    {
        // Given a User of a scope
        var scope = await SeedScopeAsync();
        var person = await SeedPersonAsync(p =>
        {
            p.RoleId = (long)Roles.User;
            p.ScopeId = scope.Id;
            p.ScopeMembership = new ScopeUser { ScopeId = scope.Id };
        });

        // When promoted (UC-23) the way the handler does it: the role and the membership row targeted,
        // the ownership row inserted in the same transaction (#205)
        var stored = await StaleSaveAcrossAsync(person.Id, async (writes, _, context) =>
        {
            var current = await context.Persons
                .Include(x => x.ScopeMembership)
                .SingleAsync(x => x.Id == person.Id);

            Assert.Equal(AccountChangeOutcome.Changed, await writes.TryChangeEmailAndRoleAsync(
                current, current.Email, (long)Roles.ScopeAdmin, Now(),
                () => writes.TryAddScopeOwnerAsync(current, scope.Id)));

            // A later save in the same unit of work finds nothing of the change left to write
            await context.SaveChangesAsync();
        });

        // Then the role, the cleared scope, and both join-row edits stand
        Assert.Equal((long)Roles.ScopeAdmin, stored.RoleId);
        Assert.Null(stored.ScopeId);
        Assert.Null(stored.ScopeMembership);
        Assert.Equal(scope.Id, Assert.Single(stored.ScopeOwnerships).ScopeId);
    }

    [FunctionalFact]
    public async Task GivenACopyReadBeforeVerification_WhenSavedAsAWholeRow_ThenTheAddressStaysVerified()
    {
        // Given
        var person = await SeedPersonAsync(p => p.EmailVerified = false);

        // When
        var stored = await StaleSaveAcrossAsync(person.Id, (writes, current) =>
            writes.TryMarkEmailVerifiedAsync(current, Now()));

        // Then
        Assert.True(stored.EmailVerified);
    }

    [FunctionalFact]
    public async Task GivenACopyReadBeforeALockout_WhenSavedAsAWholeRow_ThenTheLockoutStands()
    {
        // Given
        var person = await SeedPersonAsync();
        var now = Now();
        var lockedOutUntil = now.AddMinutes(15);

        // When the attempt that reaches the limit locks the account
        var stored = await StaleSaveAcrossAsync(person.Id, async (writes, current) =>
            Assert.True(await writes.TryReserveLoginAttemptAsync(current, 1, now, lockedOutUntil)));

        // Then a stale save no longer clears it
        Assert.NotNull(stored.LockedOutUntil);
        Assert.True(stored.LockedOutUntil > now);
    }

    [FunctionalFact]
    public async Task GivenACopyReadWhileLockedOut_WhenSavedAfterTheLockoutWasCleared_ThenItStaysCleared()
    {
        // Given
        var person = await SeedPersonAsync(p =>
        {
            p.FailedLoginAttempts = 4;
            p.LockedOutUntil = Now().AddMinutes(15);
        });

        // When
        var stored = await StaleSaveAcrossAsync(person.Id, (writes, current) => writes.ClearLoginAttemptsAsync(current));

        // Then
        Assert.Equal(0, stored.FailedLoginAttempts);
        Assert.Null(stored.LockedOutUntil);
    }

    [FunctionalFact]
    public async Task GivenARestrictionAlreadyImposed_WhenRestrictingFromAStaleRead_ThenTheFirstStands()
    {
        // Given a copy read while unrestricted, and a restriction imposed after
        var person = await SeedPersonAsync();
        var first = Now().AddMinutes(-1);

        await using var stale = db.CreateContext();
        var staleCopy = await stale.Persons.SingleAsync(x => x.Id == person.Id);

        await using (var other = db.CreateContext())
        {
            var current = await other.Persons.SingleAsync(x => x.Id == person.Id);
            Assert.True(await new AtomicWrites(other)
                .TryRestrictProcessingAsync(current, (int)RestrictionGrounds.ObjectionPending, first));
        }

        // When
        var restricted = await new AtomicWrites(stale)
            .TryRestrictProcessingAsync(staleCopy, (int)RestrictionGrounds.AccuracyContested, Now());

        // Then
        Assert.False(restricted);
        var stored = await StoredAsync(person.Id);
        Assert.Equal(first, stored.ProcessingRestrictedAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal((int)RestrictionGrounds.ObjectionPending, stored.RestrictionGround);
    }

    [FunctionalFact]
    public async Task GivenAnAddressChangedSinceTheRead_WhenChangingItFromTheStaleRead_ThenNothingIsWritten()
    {
        // Given
        var person = await SeedPersonAsync();
        var otherEmail = $"other-{Guid.NewGuid():N}@test.local";

        await using var stale = db.CreateContext();
        var staleCopy = await stale.Persons.SingleAsync(x => x.Id == person.Id);

        await using (var other = db.CreateContext())
        {
            var current = await other.Persons.SingleAsync(x => x.Id == person.Id);
            await new AtomicWrites(other).TryChangeEmailAndRoleAsync(
                current, otherEmail, current.RoleId, Now(), () => Task.FromResult(true));
        }

        var saved = false;

        // When
        var outcome = await new AtomicWrites(stale).TryChangeEmailAndRoleAsync(
            staleCopy, $"mine-{Guid.NewGuid():N}@test.local", staleCopy.RoleId, Now(), () =>
            {
                saved = true;
                return Task.FromResult(true);
            });

        // Then
        Assert.Equal(AccountChangeOutcome.Stale, outcome);
        Assert.False(saved);
        Assert.Equal(otherEmail, (await StoredAsync(person.Id)).Email);
    }

    [FunctionalFact]
    public async Task GivenAnAddressAnotherAdminHolds_WhenChangingToIt_ThenAddressTakenAndNothingIsWritten()
    {
        // Given two live admins
        var holder = await SeedPersonAsync();
        var person = await SeedPersonAsync();

        await using var context = db.CreateContext();
        var current = await context.Persons.SingleAsync(x => x.Id == person.Id);

        // When
        var outcome = await new AtomicWrites(context).TryChangeEmailAndRoleAsync(
            current, holder.Email, current.RoleId, Now(), () => Task.FromResult(true));

        // Then the admin uniqueness index refuses it (FR-PE-09)
        Assert.Equal(AccountChangeOutcome.AddressTaken, outcome);
        Assert.Equal(person.Email, (await StoredAsync(person.Id)).Email);
    }

    [FunctionalFact]
    public async Task GivenALiveAdmin_WhenAddingAnOwnership_ThenTheRowIsWrittenOnceAndTheProfileIsNot()
    {
        // Given an admin renamed by another request after this one read them
        var scope = await SeedScopeAsync();
        var person = await SeedPersonAsync();

        await using var context = db.CreateContext();
        var current = await context.Persons.Include(x => x.ScopeOwnerships).SingleAsync(x => x.Id == person.Id);

        await using (var other = db.CreateContext())
        {
            await other.Persons.Where(x => x.Id == person.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Name, "Renamed meanwhile"));
        }

        // When
        var added = await new AtomicWrites(context).TryAddScopeOwnerAsync(current, scope.Id);
        await context.SaveChangesAsync();

        // Then the row is written, a later save of the same unit of work does not write it again, and
        // the rename made meanwhile is not overwritten
        Assert.True(added);
        Assert.Contains(current.ScopeOwnerships, x => x.ScopeId == scope.Id);
        var stored = await StoredAsync(person.Id);
        Assert.Equal(scope.Id, Assert.Single(stored.ScopeOwnerships).ScopeId);
        Assert.Equal("Renamed meanwhile", stored.Name);
    }

    [FunctionalFact]
    public async Task GivenAnAdminDeletedSinceTheRead_WhenAddingAnOwnership_ThenNoRowIsWritten()
    {
        // Given
        var scope = await SeedScopeAsync();
        var person = await SeedPersonAsync();

        await using var context = db.CreateContext();
        var current = await context.Persons.Include(x => x.ScopeOwnerships).SingleAsync(x => x.Id == person.Id);

        await using (var other = db.CreateContext())
        {
            var copy = await other.Persons.SingleAsync(x => x.Id == person.Id);
            await new AtomicWrites(other).TryMarkDeletedAsync(copy, DeletionKinds.Administrative, Now());
        }

        // When
        var added = await new AtomicWrites(context).TryAddScopeOwnerAsync(current, scope.Id);

        // Then
        Assert.False(added);
        Assert.Empty((await StoredAsync(person.Id)).ScopeOwnerships);
    }

    [FunctionalFact]
    public async Task GivenTheLastLiveCoOwnerDeletedSinceTheCheck_WhenRemovingAnOwnership_ThenTheRowStays()
    {
        // Given a scope owned by two admins, the other of whom is deleted after this request checked
        var scope = await SeedScopeAsync();
        var person = await SeedPersonAsync(p => p.ScopeOwnerships = [new ScopeOwner { ScopeId = scope.Id }]);
        var coOwner = await SeedPersonAsync(p => p.ScopeOwnerships = [new ScopeOwner { ScopeId = scope.Id }]);

        await using var context = db.CreateContext();
        var current = await context.Persons.Include(x => x.ScopeOwnerships).SingleAsync(x => x.Id == person.Id);

        await using (var other = db.CreateContext())
        {
            var copy = await other.Persons.SingleAsync(x => x.Id == coOwner.Id);
            await new AtomicWrites(other).TryMarkDeletedAsync(copy, DeletionKinds.Administrative, Now());
        }

        // When
        var removed = await new AtomicWrites(context).TryRemoveScopeOwnerAsync(current, scope.Id);

        // Then the scope keeps its owner (NFR-12)
        Assert.False(removed);
        Assert.Single((await StoredAsync(person.Id)).ScopeOwnerships);
    }

    [FunctionalFact]
    public async Task GivenALiveCoOwner_WhenRemovingAnOwnership_ThenOnlyThatRowGoes()
    {
        // Given
        var scope = await SeedScopeAsync();
        var person = await SeedPersonAsync(p => p.ScopeOwnerships = [new ScopeOwner { ScopeId = scope.Id }]);
        var coOwner = await SeedPersonAsync(p => p.ScopeOwnerships = [new ScopeOwner { ScopeId = scope.Id }]);

        await using var context = db.CreateContext();
        var current = await context.Persons.Include(x => x.ScopeOwnerships).SingleAsync(x => x.Id == person.Id);

        // When
        var removed = await new AtomicWrites(context).TryRemoveScopeOwnerAsync(current, scope.Id);
        await context.SaveChangesAsync();

        // Then
        Assert.True(removed);
        Assert.Empty(current.ScopeOwnerships);
        Assert.Empty((await StoredAsync(person.Id)).ScopeOwnerships);
        Assert.Single((await StoredAsync(coOwner.Id)).ScopeOwnerships);
    }
}
