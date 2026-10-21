using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Data.Persistence;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Hashing;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     Two confirmations of one pending registration (UC-47) whose claims meet, against the real database and
///     as the runtime login (#209): the second waits on the first's claim and, once that commits, answers
///     "already used" — one account, never two.
/// </summary>
/// <remarks>
///     <para>
///         Both requests are made to reach the claim. The second is held just before its claim
///         (<see cref="PauseBefore" />), past its guess charge and password check; the first then claims the
///         row, creates the person and is held at its commit (<see cref="PauseBeforeCommit" />), its claim still
///         uncommitted. The second is let go, and the test waits until PostgreSQL reports it blocked on the
///         claimed row before letting the first commit.
///     </para>
///     <para>
///         The order matters: started after the first claim, the second would block at its guess charge, an
///         update of the same row, and find the row gone there instead — a different path.
///     </para>
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class PendingRegistrationClaimRaceTests(PostgresFixture db)
{
    private const string Password = "Str0ng-Claim-Race-Pass!";

    [FunctionalFact]
    public async Task GivenAClaimHeldUncommitted_WhenTheSameRegistrationIsConfirmedAgain_ThenItWaitsAndIsAlreadyClaimed()
    {
        // Given a pending registration, and a second confirmation of it held just before its claim
        var (scope, email, tokenHash) = await SeedPendingAsync();
        var beforeClaim = new PauseBefore("DELETE FROM heimdall.pending_registration");
        var beforeCommit = new PauseBeforeCommit();
        await using var second = Runtime(beforeClaim);
        await using var first = Runtime(beforeCommit);
        var secondPid = await LockWaits.OpenAndGetPidAsync(second);

        var secondConfirmation = Confirmer(second).ConfirmAsync(tokenHash, Password, DateTime.UtcNow);
        await beforeClaim.Reached.WaitAsync(TimeSpan.FromSeconds(30));

        // When the first claims the row and creates the person, and holds its commit while the second claims
        var firstConfirmation = Confirmer(first).ConfirmAsync(tokenHash, Password, DateTime.UtcNow);
        await beforeCommit.Reached.WaitAsync(TimeSpan.FromSeconds(30));
        beforeClaim.Resume();
        var blocked = await LockWaits.WaitUntilBlockedOrDoneAsync(db, secondPid, secondConfirmation);
        beforeCommit.Resume();

        // Then the second waited on the claim, the first created the account, and the second was told the
        // link is already used
        Assert.True(blocked);
        Assert.Equal(PendingRegistrationOutcome.Confirmed, (await firstConfirmation).Outcome);
        Assert.Equal(PendingRegistrationOutcome.AlreadyClaimed, (await secondConfirmation).Outcome);

        await using var check = db.CreateContext();
        Assert.Equal(1, await check.Persons.CountAsync(person => person.Email == email && person.ScopeId == scope.Id));
        Assert.False(await check.PendingRegistrations.AnyAsync(pending => pending.TokenHash == tokenHash));
    }

    private AppDbContext Runtime(params IInterceptor[] interceptors) =>
        PostgresFixture.CreateContext(db.RuntimeConnectionString, interceptors);

    private static PendingRegistrationConfirmer Confirmer(AppDbContext context)
    {
        var pending = new EfRepository<PendingRegistration, long>(context);
        var persons = new EfRepository<Person, long>(context);

        return new PendingRegistrationConfirmer(pending, pending, persons,
            new EfRepository<GoogleUser, long>(context), persons, new AtomicWrites(context),
            new WriteTransactions(context), NullLogger<PendingRegistrationConfirmer>.Instance);
    }

    private async Task<(Scope Scope, string Email, string TokenHash)> SeedPendingAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope
        {
            PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}", SelfRegistrationEnabled = true
        };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        var email = $"claim-race-{Guid.NewGuid():N}@test.local";
        var tokenHash = SingleUseTokenHash.Of(SingleUseToken.New());
        context.PendingRegistrations.Add(new PendingRegistration
        {
            ScopeId = scope.Id,
            Name = "Claimed Person",
            Email = email,
            PasswordHash = Hash.EncodeWithRandomSalt(Password, out var salt),
            Salt = salt,
            TokenHash = tokenHash,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        return (scope, email, tokenHash);
    }
}
