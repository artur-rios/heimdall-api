using ArturRios.IdentityManager.Command.Handlers;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for DeleteScopeCommandHandler (UC-04).
// Cover the main flow, the cascade to Users/Google Users/applications, AF-04a (not found), and
// AF-04b (already deleted, idempotent). Authorization (403/401) is a functional concern.
public class DeleteScopeCommandHandlerTests
{
    // One fake per aggregate; each is passed as BOTH the reader and the writer argument.
    private sealed record Fakes(
        AsyncFakeRepository<Scope> Scopes,
        AsyncFakeRepository<Person> Persons,
        AsyncFakeRepository<GoogleUser> GoogleUsers,
        AsyncFakeRepository<Application> Applications)
    {
        public DeleteScopeCommandHandler Handler() => new(
            Scopes, Scopes, Persons, Persons, GoogleUsers, GoogleUsers, Applications, Applications);
    }

    private static async Task<Fakes> EmptyFakes()
    {
        await Task.CompletedTask;
        return new Fakes(
            new AsyncFakeRepository<Scope>(),
            new AsyncFakeRepository<Person>(),
            new AsyncFakeRepository<GoogleUser>(),
            new AsyncFakeRepository<Application>());
    }

    private static async Task<Scope> SeedScopeAsync(Fakes fakes, bool isDeleted = false)
    {
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}", IsDeleted = isDeleted };
        await fakes.Scopes.CreateAsync(scope);
        return scope;
    }

    private static async Task SeedUserAsync(Fakes fakes, long scopeId, bool isDeleted = false)
    {
        // ScopeMembership is set at construction (the handler's query only reads ScopeMembership.ScopeId),
        // so it is stored with the person regardless of how the fake handles the reference.
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            IsDeleted = isDeleted,
            ScopeMembership = new ScopeUser { ScopeId = scopeId }
        };
        await fakes.Persons.CreateAsync(person);
    }

    private static async Task SeedGoogleUserAsync(Fakes fakes, long scopeId, bool isDeleted = false)
    {
        await fakes.GoogleUsers.CreateAsync(new GoogleUser { PublicId = Guid.NewGuid(), ScopeId = scopeId, IsDeleted = isDeleted });
    }

    private static async Task SeedApplicationAsync(Fakes fakes, long scopeId, bool isDeleted = false)
    {
        await fakes.Applications.CreateAsync(new Application { PublicId = Guid.NewGuid(), ScopeId = scopeId, IsDeleted = isDeleted });
    }

    [UnitFact]
    public async Task GivenScopeWithNoMembers_WhenHandlingDeleteScope_ThenScopeIsDeletedWithZeroCounts()
    {
        // Given
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        var command = new DeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — output
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.Equal(scope.PublicId, output.Data!.Id);
        Assert.Equal(0, output.Data.DeletedUserCount);
        Assert.Equal(0, output.Data.DeletedGoogleUserCount);
        Assert.Equal(0, output.Data.DeletedApplicationCount);
        Assert.Contains(ScopeMessages.ScopeDeletedSuccessfully, output.Messages);

        // Then — the scope is flipped in the store
        var stored = (await fakes.Scopes.GetAllAsync()).Data!.Single();
        Assert.True(stored.IsDeleted);
    }

    [UnitFact]
    public async Task GivenScopeWithMembers_WhenHandlingDeleteScope_ThenMembersAreLogicallyDeletedAndCounted()
    {
        // Given a scope with two Users, one Google User, and one application
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        await SeedUserAsync(fakes, scope.Id);
        await SeedUserAsync(fakes, scope.Id);
        await SeedGoogleUserAsync(fakes, scope.Id);
        await SeedApplicationAsync(fakes, scope.Id);
        var command = new DeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — counts reflect the totals
        Assert.True(output.Success);
        Assert.Equal(2, output.Data!.DeletedUserCount);
        Assert.Equal(1, output.Data.DeletedGoogleUserCount);
        Assert.Equal(1, output.Data.DeletedApplicationCount);

        // Then — every member is flipped
        Assert.All((await fakes.Persons.GetAllAsync()).Data!, p => Assert.True(p.IsDeleted));
        Assert.All((await fakes.GoogleUsers.GetAllAsync()).Data!, g => Assert.True(g.IsDeleted));
        Assert.All((await fakes.Applications.GetAllAsync()).Data!, a => Assert.True(a.IsDeleted));
    }

    [UnitFact]
    public async Task GivenScopeWithAlreadyDeletedMember_WhenHandlingDeleteScope_ThenMemberStillCounted()
    {
        // Given a scope whose single User is already individually logically deleted
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        await SeedUserAsync(fakes, scope.Id, isDeleted: true);
        var command = new DeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — the already-deleted User is still part of the total
        Assert.True(output.Success);
        Assert.Equal(1, output.Data!.DeletedUserCount);
    }

    [UnitFact]
    public async Task GivenMissingScope_WhenHandlingDeleteScope_ThenReturnsScopeNotFound()
    {
        // Given an empty store
        var fakes = await EmptyFakes();
        var command = new DeleteScopeCommand { Id = Guid.NewGuid() };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenAlreadyDeletedScope_WhenHandlingDeleteScope_ThenSucceedsIdempotentlyWithTotals()
    {
        // Given a logically deleted scope that still has one application
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes, isDeleted: true);
        await SeedApplicationAsync(fakes, scope.Id, isDeleted: true);
        var command = new DeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — idempotent success, totals still reported
        Assert.True(output.Success);
        Assert.Equal(scope.PublicId, output.Data!.Id);
        Assert.Equal(1, output.Data.DeletedApplicationCount);
        Assert.Contains(ScopeMessages.ScopeDeletedSuccessfully, output.Messages);
    }
}
