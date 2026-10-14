using ArturRios.Data.Relational.Core.Entities;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Input.Validation;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for ListPendingRegistrationsQueryHandler (UC-49, FR-SR-11): one scope's rows only, newest
// first with the id as tiebreaker, the projection (attemptsLeft floored at 0, expired at ExpiresAt <=
// now), a closed scope still listed, AF-49a (unknown or deleted scope), AF-49b (not an owner), AF-49c
// (none pending → empty page) and the page bound.
public class ListPendingRegistrationsQueryHandlerTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static Scope Scope(long id, bool isDeleted = false, bool open = true) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"scope-{id}",
        IsDeleted = isDeleted,
        SelfRegistrationEnabled = open
    };

    private static PendingRegistration Pending(
        long id, Scope scope, DateTime? createdAt = null, DateTime? expiresAt = null, int failedAttempts = 0) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        ScopeId = scope.Id,
        Scope = scope,
        Name = $"Registrant {id}",
        Email = $"registrant-{id}@test.local",
        PasswordHash = [1],
        Salt = [2],
        TokenHash = $"hash-{id}",
        CreatedAt = createdAt ?? Now.AddMinutes(-id),
        ExpiresAt = expiresAt ?? Now.AddHours(1),
        FailedAttempts = failedAttempts
    };

    private static async Task<AsyncFakeRepository<T, long>> RepositoryWith<T>(params T[] items) where T : Entity<long>
    {
        var repository = new AsyncFakeRepository<T, long>();

        foreach (var item in items)
        {
            await repository.CreateAsync(item);
        }

        return repository;
    }

    private static IScopeOwnershipChecker Ownership(bool allowed)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);

        return checker.Object;
    }

    private static ListPendingRegistrationsQueryHandler Handler(
        AsyncFakeRepository<Scope, long> scopes,
        AsyncFakeRepository<PendingRegistration, long> pending,
        bool ownershipAllowed = true) =>
        new(scopes, pending, Ownership(ownershipAllowed), new ListPendingRegistrationsQueryValidator());

    private static ListPendingRegistrationsQuery Query(Scope scope, int pageSize = 20) => new()
    {
        ScopeId = scope.PublicId,
        PageNumber = 1,
        PageSize = pageSize,
        ActingPersonId = Guid.NewGuid(),
        ActingRole = (int)Roles.ScopeAdmin
    };

    [UnitFact]
    public async Task GivenPendingRows_WhenListing_ThenOnlyThatScopesRowsAreReturnedNewestFirst()
    {
        // Given two rows in the scope, created ten minutes apart, and one in another scope
        var scope = Scope(1);
        var other = Scope(2);
        var older = Pending(1, scope, createdAt: Now.AddMinutes(-20));
        var newer = Pending(2, scope, createdAt: Now.AddMinutes(-10));
        var handler = Handler(await RepositoryWith(scope, other), await RepositoryWith(older, Pending(3, other), newer));

        // When
        var output = await handler.HandleAsync(Query(scope));

        // Then
        Assert.True(output.Success);
        Assert.Contains(ScopeMessages.PendingRegistrationsRetrieved, output.Messages);
        Assert.Equal(2, output.TotalItems);
        Assert.Equal([newer.PublicId, older.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenRowsCreatedAtOneInstant_WhenListing_ThenTheLaterIdComesFirst()
    {
        // Given two registrations stored in the same instant
        var scope = Scope(1);
        var first = Pending(1, scope, createdAt: Now);
        var second = Pending(2, scope, createdAt: Now);
        var handler = Handler(await RepositoryWith(scope), await RepositoryWith(first, second));

        // When
        var output = await handler.HandleAsync(Query(scope));

        // Then
        Assert.Equal([second.PublicId, first.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenARow_WhenListing_ThenItIsProjectedWithoutSecrets()
    {
        // Given one live registration with three wrong guesses spent
        var scope = Scope(1);
        var row = Pending(1, scope, createdAt: Now.AddMinutes(-5), expiresAt: Now.AddHours(23), failedAttempts: 3);
        var handler = Handler(await RepositoryWith(scope), await RepositoryWith(row));

        // When
        var output = await handler.HandleAsync(Query(scope));

        // Then
        var item = Assert.Single(output.Data!);
        Assert.Equal(row.PublicId, item.Id);
        Assert.Equal(row.Name, item.Name);
        Assert.Equal(row.Email, item.Email);
        Assert.Equal(row.CreatedAt, item.CreatedAt);
        Assert.Equal(row.ExpiresAt, item.ExpiresAt);
        Assert.Equal(PendingRegistration.MaxConfirmationGuesses - 3, item.AttemptsLeft);
        Assert.False(item.Expired);
    }

    [UnitFact]
    public void GivenTheOutputType_WhenInspected_ThenItCarriesNoSecretOrCounter()
    {
        // Given / When
        var names = typeof(PendingRegistrationOutput).GetProperties().Select(property => property.Name).ToList();

        // Then — FR-SR-11: never the hash, the salt or the token hash
        Assert.DoesNotContain(nameof(PendingRegistration.PasswordHash), names);
        Assert.DoesNotContain(nameof(PendingRegistration.Salt), names);
        Assert.DoesNotContain(nameof(PendingRegistration.TokenHash), names);
        Assert.DoesNotContain(nameof(PendingRegistration.FailedAttempts), names);
    }

    [UnitTheory]
    [InlineData(10)]
    [InlineData(12)]
    public async Task GivenTheBudgetSpentOrOverspent_WhenListing_ThenAttemptsLeftIsZero(int failedAttempts)
    {
        // Given a row whose budget is spent — or overspent, as parallel guesses can leave it
        var scope = Scope(1);
        var handler = Handler(
            await RepositoryWith(scope), await RepositoryWith(Pending(1, scope, failedAttempts: failedAttempts)));

        // When
        var output = await handler.HandleAsync(Query(scope));

        // Then — never below zero
        Assert.Equal(0, Assert.Single(output.Data!).AttemptsLeft);
    }

    [UnitFact]
    public async Task GivenAnExpiredRow_WhenListing_ThenItIsListedAndMarkedExpired()
    {
        // Given a row past its expiry that the purge has not yet removed
        var scope = Scope(1);
        var handler = Handler(
            await RepositoryWith(scope), await RepositoryWith(Pending(1, scope, expiresAt: Now.AddSeconds(-1))));

        // When
        var output = await handler.HandleAsync(Query(scope));

        // Then
        Assert.True(Assert.Single(output.Data!).Expired);
    }

    [UnitFact]
    public async Task GivenAScopeClosedToSelfRegistration_WhenListing_ThenItsPendingRowsAreStillListed()
    {
        // Given self-registration switched off after someone signed up — exactly when an owner wants
        // to see what is left
        var scope = Scope(1, open: false);
        var row = Pending(1, scope);
        var handler = Handler(await RepositoryWith(scope), await RepositoryWith(row));

        // When
        var output = await handler.HandleAsync(Query(scope));

        // Then
        Assert.True(output.Success);
        Assert.Equal([row.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenNoPendingRows_WhenListing_ThenAnEmptyPageIsASuccess()
    {
        // Given AF-49c
        var scope = Scope(1);
        var handler = Handler(await RepositoryWith(scope), await RepositoryWith<PendingRegistration>());

        // When
        var output = await handler.HandleAsync(Query(scope));

        // Then
        Assert.True(output.Success);
        Assert.Equal(0, output.TotalItems);
        Assert.Empty(output.Data!);
    }

    [UnitFact]
    public async Task GivenAnUnknownScope_WhenListing_ThenReturnsScopeNotFound()
    {
        // Given AF-49a
        var handler = Handler(await RepositoryWith(Scope(1)), await RepositoryWith<PendingRegistration>());

        // When
        var output = await handler.HandleAsync(Query(Scope(99)));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenALogicallyDeletedScope_WhenListing_ThenReturnsScopeNotFound()
    {
        // Given AF-49a — answered as the scope's owners listing answers a deleted scope
        var scope = Scope(1, isDeleted: true);
        var handler = Handler(await RepositoryWith(scope), await RepositoryWith(Pending(1, scope)));

        // When
        var output = await handler.HandleAsync(Query(scope));

        // Then
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenACallerWhoDoesNotOwnTheScope_WhenListing_ThenReturnsNotScopeOwner()
    {
        // Given AF-49b
        var scope = Scope(1);
        var handler = Handler(await RepositoryWith(scope), await RepositoryWith(Pending(1, scope)), ownershipAllowed: false);

        // When
        var output = await handler.HandleAsync(Query(scope));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.NotScopeOwner, output.Errors);
    }

    [UnitFact]
    public async Task GivenPageSizeAboveTheBound_WhenListing_ThenReturnsInvalidPageSize()
    {
        // Given NFR-10
        var scope = Scope(1);
        var handler = Handler(await RepositoryWith(scope), await RepositoryWith<PendingRegistration>());

        // When
        var output = await handler.HandleAsync(Query(scope, pageSize: 101));

        // Then
        Assert.Contains(PaginationMessages.InvalidPageSize, output.Errors);
    }
}
