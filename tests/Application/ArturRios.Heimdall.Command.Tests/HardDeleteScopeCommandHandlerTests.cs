using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for HardDeleteScopeCommandHandler (UC-05).
// Cover the main flow, the explicit cascade to Users/Google Users/applications, and AF-05a (not
// found). Authorization (403/401) and the SCOPE_OWNER/SCOPE_USER join-row cascade are functional
// concerns (the fake repositories are not join-aware).
public class HardDeleteScopeCommandHandlerTests
{
    // One fake per aggregate; each is passed as BOTH the reader and the writer argument.
    private sealed record Fakes(
        AsyncFakeRepository<Scope> Scopes,
        AsyncFakeRepository<Person> Persons,
        AsyncFakeRepository<GoogleUser> GoogleUsers,
        AsyncFakeRepository<Application> Applications)
    {
        public HardDeleteScopeCommandHandler Handler() => new(
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
        // ScopeMembership is set at construction (the handler's query only reads ScopeMembership.ScopeId).
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
    public async Task GivenScopeWithNoMembers_WhenHandlingHardDeleteScope_ThenScopeIsRemovedWithZeroCounts()
    {
        // Given
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        var command = new HardDeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — output
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.Equal(scope.PublicId, output.Data!.Id);
        Assert.Equal(0, output.Data.UserCount);
        Assert.Equal(0, output.Data.GoogleUserCount);
        Assert.Equal(0, output.Data.ApplicationCount);
        Assert.Contains(ScopeMessages.ScopeHardDeletedSuccessfully, output.Messages);

        // Then — the scope is gone from the store
        Assert.Empty((await fakes.Scopes.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenScopeWithMembers_WhenHandlingHardDeleteScope_ThenMembersAreRemovedAndCounted()
    {
        // Given a scope with two Users, one Google User, and one application
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        await SeedUserAsync(fakes, scope.Id);
        await SeedUserAsync(fakes, scope.Id);
        await SeedGoogleUserAsync(fakes, scope.Id);
        await SeedApplicationAsync(fakes, scope.Id);
        var command = new HardDeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — counts reflect the totals
        Assert.True(output.Success);
        Assert.Equal(2, output.Data!.UserCount);
        Assert.Equal(1, output.Data.GoogleUserCount);
        Assert.Equal(1, output.Data.ApplicationCount);

        // Then — the scope and every member are removed from their stores
        Assert.Empty((await fakes.Scopes.GetAllAsync()).Data!);
        Assert.Empty((await fakes.Persons.GetAllAsync()).Data!);
        Assert.Empty((await fakes.GoogleUsers.GetAllAsync()).Data!);
        Assert.Empty((await fakes.Applications.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenScopeWithAlreadyDeletedMember_WhenHandlingHardDeleteScope_ThenMemberStillCountedAndRemoved()
    {
        // Given a scope whose single User is already individually logically deleted
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        await SeedUserAsync(fakes, scope.Id, isDeleted: true);
        var command = new HardDeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — the already-deleted User is still counted and still removed
        Assert.True(output.Success);
        Assert.Equal(1, output.Data!.UserCount);
        Assert.Empty((await fakes.Persons.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenAlreadyLogicallyDeletedScope_WhenHandlingHardDeleteScope_ThenScopeIsRemoved()
    {
        // Given a logically deleted scope that still has one application
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes, isDeleted: true);
        await SeedApplicationAsync(fakes, scope.Id, isDeleted: true);
        var command = new HardDeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — the scope is hard-deleted regardless of its logical-deletion state
        Assert.True(output.Success);
        Assert.Equal(scope.PublicId, output.Data!.Id);
        Assert.Equal(1, output.Data.ApplicationCount);
        Assert.Empty((await fakes.Scopes.GetAllAsync()).Data!);
        Assert.Empty((await fakes.Applications.GetAllAsync()).Data!);
        Assert.Contains(ScopeMessages.ScopeHardDeletedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenMissingScope_WhenHandlingHardDeleteScope_ThenReturnsScopeNotFound()
    {
        // Given an empty store
        var fakes = await EmptyFakes();
        var command = new HardDeleteScopeCommand { Id = Guid.NewGuid() };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
    }
}
