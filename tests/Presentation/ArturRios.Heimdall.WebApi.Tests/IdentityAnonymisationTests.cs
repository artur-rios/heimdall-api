using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     The anonymisation pass (NFR-20) against the real database. Two things here cannot be shown
///     over in-memory fakes, and both would fail in production rather than in a unit test.
/// </summary>
/// <remarks>
///     <para>
///         The first is translation. The pass selects on a conditional — the deadline depends on the
///         record's <c>DeletionKind</c> — and a fake evaluates that in memory whatever EF would have
///         made of it.
///     </para>
///     <para>
///         The second is the unique indexes. <c>GOOGLE_USER</c> carries unconditional unique indexes
///         on <c>(scope_id, google_id)</c> and <c>(scope_id, LOWER(email))</c>, and no fake enforces
///         them, so a constant placeholder would pass every unit test and fail on the second Google
///         User anonymised in a scope. This seeds two deliberately.
///     </para>
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class IdentityAnonymisationTests(PostgresFixture fixture)
{
    private static readonly DataRetentionOptions Retention = new()
    {
        SubjectErasureDeadline = TimeSpan.FromDays(30),
        AdministrativeDeletionWindow = TimeSpan.FromDays(90),
        PurgeBatchSize = 500
    };

    private static AnonymiseExpiredDeletionsCommandHandler Handler(AppDbContext context)
    {
        var persons = new EfRepository<Person>(context);
        var googleUsers = new EfRepository<GoogleUser>(context);
        var passwordResetTokens = new EfRepository<PasswordResetToken>(context);
        var emailVerificationTokens = new EfRepository<EmailVerificationToken>(context);
        var twoFactorAuths = new EfRepository<TwoFactorAuth>(context);

        return new AnonymiseExpiredDeletionsCommandHandler(
            persons, persons, googleUsers, googleUsers,
            passwordResetTokens, passwordResetTokens,
            emailVerificationTokens, emailVerificationTokens,
            twoFactorAuths, twoFactorAuths,
            Retention);
    }

    private static Person DeletedPerson(DateTime deletedAt, DeletionKinds kind) => new()
    {
        PublicId = Guid.NewGuid(),
        Name = "Ada Lovelace",
        Email = $"ada-{Guid.NewGuid():N}@functional.test",
        PasswordHash = [1, 2, 3],
        Salt = [4, 5, 6],
        RoleId = (long)Roles.User,
        EmailVerified = true,
        IsDeleted = true,
        DeletedAt = deletedAt,
        DeletionKind = (int)kind
    };

    [FunctionalFact]
    public async Task GivenRecordsPastTheirWindow_WhenAnonymising_ThenTheDatabaseHoldsNoIdentifyingValue()
    {
        await using var context = fixture.CreateContext();

        // A scope of this test's own, so the two Google Users below collide with each other's
        // placeholders if they collide at all — which is the point
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"Retention {Guid.NewGuid():N}" };

        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        var administrative = DeletedPerson(DateTime.UtcNow.AddDays(-120), DeletionKinds.Administrative);
        var requested = DeletedPerson(DateTime.UtcNow.AddDays(-45), DeletionKinds.SubjectRequested);

        // 45 days: past the statutory deadline, inside the reversal window. It is the record that
        // proves the conditional actually reached SQL — if the deadline were applied uniformly, one
        // of this pair comes out wrong.
        var stillInsideItsWindow = DeletedPerson(DateTime.UtcNow.AddDays(-45), DeletionKinds.Administrative);

        var firstGoogleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(), GoogleId = Guid.NewGuid().ToString("N"), Name = "Ada",
            Email = $"g1-{Guid.NewGuid():N}@functional.test", ScopeId = scope.Id, IsDeleted = true,
            ProfilePictureUrl = "https://example.invalid/photo.jpg",
            DeletedAt = DateTime.UtcNow.AddDays(-120), DeletionKind = (int)DeletionKinds.Administrative
        };
        var secondGoogleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(), GoogleId = Guid.NewGuid().ToString("N"), Name = "Grace",
            Email = $"g2-{Guid.NewGuid():N}@functional.test", ScopeId = scope.Id, IsDeleted = true,
            ProfilePictureUrl = "https://example.invalid/photo2.jpg",
            DeletedAt = DateTime.UtcNow.AddDays(-120), DeletionKind = (int)DeletionKinds.Administrative
        };

        context.Persons.AddRange(administrative, requested, stillInsideItsWindow);
        context.GoogleUsers.AddRange(firstGoogleUser, secondGoogleUser);
        await context.SaveChangesAsync();

        context.PasswordResetTokens.Add(new PasswordResetToken
        {
            PersonId = administrative.Id, TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        });
        context.TwoFactorAuths.Add(new TwoFactorAuth
        {
            PersonId = administrative.Id, AppEnabled = true, IsActive = true, TotpSecretEncrypted = [7, 7, 7]
        });
        await context.SaveChangesAsync();

        // When
        var output = await Handler(context).HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.True(output.Success);

        await using var verification = fixture.CreateContext();

        // The two due persons carry no identifying value; both unique indexes accepted the writes
        foreach (var due in new[] { administrative, requested })
        {
            var stored = await verification.Persons.SingleAsync(x => x.Id == due.Id);

            Assert.Equal(IdentityAnonymiser.AnonymisedName, stored.Name);
            Assert.EndsWith($"@{IdentityAnonymiser.AnonymisedEmailDomain}", stored.Email);
            Assert.Empty(stored.PasswordHash);
            Assert.Empty(stored.Salt);
            Assert.False(stored.EmailVerified);
            Assert.NotNull(stored.AnonymisedAt);

            // NFR-07: the row is still there and still resolvable
            Assert.Equal(due.PublicId, stored.PublicId);
            Assert.True(stored.IsDeleted);
        }

        // The administrative deletion inside its window is untouched — the conditional reached SQL
        var untouched = await verification.Persons.SingleAsync(x => x.Id == stillInsideItsWindow.Id);

        Assert.Equal("Ada Lovelace", untouched.Name);
        Assert.Null(untouched.AnonymisedAt);

        // Both Google Users anonymised in one scope, so the placeholders did not collide
        var storedGoogleUsers = await verification.GoogleUsers
            .Where(x => x.ScopeId == scope.Id)
            .OrderBy(x => x.Id)
            .ToListAsync();

        Assert.Equal(2, storedGoogleUsers.Count);
        Assert.All(storedGoogleUsers, googleUser =>
        {
            Assert.Equal(IdentityAnonymiser.AnonymisedName, googleUser.Name);
            Assert.Null(googleUser.ProfilePictureUrl);
            Assert.NotNull(googleUser.AnonymisedAt);
        });
        Assert.NotEqual(storedGoogleUsers[0].Email, storedGoogleUsers[1].Email);
        Assert.NotEqual(storedGoogleUsers[0].GoogleId, storedGoogleUsers[1].GoogleId);

        // The dependents went with the person rather than being left behind
        Assert.False(await verification.PasswordResetTokens.AnyAsync(x => x.PersonId == administrative.Id));
        Assert.False(await verification.TwoFactorAuths.AnyAsync(x => x.PersonId == administrative.Id));
    }

    [FunctionalFact]
    public async Task GivenAnAnonymisedRecord_WhenAnonymisingAgain_ThenItIsNotTakenTwice()
    {
        await using var context = fixture.CreateContext();

        var person = DeletedPerson(DateTime.UtcNow.AddDays(-200), DeletionKinds.Administrative);

        context.Persons.Add(person);
        await context.SaveChangesAsync();

        await Handler(context).HandleAsync(new AnonymiseExpiredDeletionsCommand());

        await using var second = fixture.CreateContext();
        var firstPass = (await second.Persons.SingleAsync(x => x.Id == person.Id)).AnonymisedAt;

        await Handler(second).HandleAsync(new AnonymiseExpiredDeletionsCommand());

        await using var verification = fixture.CreateContext();
        var afterSecondPass = await verification.Persons.SingleAsync(x => x.Id == person.Id);

        // The stamp is what makes the pass idempotent: a second run must not rewrite the row
        Assert.Equal(firstPass, afterSecondPass.AnonymisedAt);
    }
}
