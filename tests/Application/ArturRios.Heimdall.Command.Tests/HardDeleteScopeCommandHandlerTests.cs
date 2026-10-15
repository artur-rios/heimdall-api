using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for HardDeleteScopeCommandHandler (UC-05).
// Cover the main flow, the explicit cascade to Users/Google Users/applications, the counted (but
// DB-cascaded) scope permissions, and AF-05a (not found). Authorization (403/401) and the
// SCOPE_OWNER/SCOPE_USER/ScopePermission cascades are functional concerns (the fake repositories
// are not join-aware). Also the tombstones (#195): one for each member person and Google User
// removed, none for an owner or the scope, all written before anything is removed.
public class HardDeleteScopeCommandHandlerTests
{
    // One fake per aggregate; each is passed as BOTH the reader and the writer argument.
    private sealed record Fakes(
        AsyncFakeRepository<Scope, long> Scopes,
        AsyncFakeRepository<Person, long> Persons,
        AsyncFakeRepository<GoogleUser, long> GoogleUsers,
        AsyncFakeRepository<Application, long> Applications,
        AsyncFakeRepository<ScopePermission, long> ScopePermissions,
        InMemoryErasedSubjects ErasedSubjects)
    {
        public HardDeleteScopeCommandHandler Handler() => new(
            Scopes, Scopes, Persons, Persons, GoogleUsers, GoogleUsers, Applications, Applications,
            ScopePermissions, ErasedSubjects);
    }

    private static async Task<Fakes> EmptyFakes()
    {
        await Task.CompletedTask;
        return new Fakes(
            new AsyncFakeRepository<Scope, long>(),
            new AsyncFakeRepository<Person, long>(),
            new AsyncFakeRepository<GoogleUser, long>(),
            new AsyncFakeRepository<Application, long>(),
            new AsyncFakeRepository<ScopePermission, long>(),
            new InMemoryErasedSubjects());
    }

    private static async Task SeedScopePermissionAsync(Fakes fakes, long scopeId, bool isDeleted = false)
    {
        await fakes.ScopePermissions.CreateAsync(new ScopePermission { PublicId = Guid.NewGuid(), ScopeId = scopeId, IsDeleted = isDeleted });
    }

    private static async Task<Scope> SeedScopeAsync(Fakes fakes, bool isDeleted = false)
    {
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}", IsDeleted = isDeleted };
        await fakes.Scopes.CreateAsync(scope);
        return scope;
    }

    private static async Task<Person> SeedUserAsync(Fakes fakes, long scopeId, bool isDeleted = false)
    {
        // ScopeMembership is set at construction (the handler's query only reads ScopeMembership.ScopeId).
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            IsDeleted = isDeleted,
            ScopeId = scopeId, ScopeMembership = new ScopeUser { ScopeId = scopeId }
        };
        await fakes.Persons.CreateAsync(person);

        return person;
    }

    private static async Task<GoogleUser> SeedGoogleUserAsync(Fakes fakes, long scopeId, bool isDeleted = false)
    {
        var googleUser = new GoogleUser { PublicId = Guid.NewGuid(), ScopeId = scopeId, IsDeleted = isDeleted };
        await fakes.GoogleUsers.CreateAsync(googleUser);

        return googleUser;
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
        Assert.Equal(0, output.Data.ScopePermissionCount);
        Assert.Contains(ScopeMessages.ScopeHardDeletedSuccessfully, output.Messages);

        // Then — the scope is gone from the store
        Assert.Empty((await fakes.Scopes.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenScopeWithMembers_WhenHandlingHardDeleteScope_ThenMembersAreRemovedAndCounted()
    {
        // Given a scope with two Users, one Google User, one application, and one scope permission
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        await SeedUserAsync(fakes, scope.Id);
        await SeedUserAsync(fakes, scope.Id);
        await SeedGoogleUserAsync(fakes, scope.Id);
        await SeedApplicationAsync(fakes, scope.Id);
        await SeedScopePermissionAsync(fakes, scope.Id);
        var command = new HardDeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — counts reflect the totals
        Assert.True(output.Success);
        Assert.Equal(2, output.Data!.UserCount);
        Assert.Equal(1, output.Data.GoogleUserCount);
        Assert.Equal(1, output.Data.ApplicationCount);
        Assert.Equal(1, output.Data.ScopePermissionCount);

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
        Assert.Empty(fakes.ErasedSubjects.Recorded);
    }

    [UnitFact]
    public async Task GivenScopeWithMembers_WhenHandlingHardDeleteScope_ThenEachRemovedMemberIsTombstonedFirst()
    {
        // Given two Users and a Google User in the scope, an owner (no membership row), and a User and
        // a Google User of another scope, watched at the moment the tombstones are written
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        var other = await SeedScopeAsync(fakes);
        var first = await SeedUserAsync(fakes, scope.Id);
        var second = await SeedUserAsync(fakes, scope.Id, isDeleted: true);
        var googleUser = await SeedGoogleUserAsync(fakes, scope.Id);
        var owner = new Person { PublicId = Guid.NewGuid(), RoleId = (long)Roles.ScopeAdmin };
        await fakes.Persons.CreateAsync(owner);
        await SeedUserAsync(fakes, other.Id);
        await SeedGoogleUserAsync(fakes, other.Id);
        await SeedApplicationAsync(fakes, scope.Id);
        var personsAtWrite = 0;
        var googleUsersAtWrite = 0;
        var applicationsAtWrite = 0;
        fakes = fakes with
        {
            ErasedSubjects = new InMemoryErasedSubjects(_ =>
            {
                personsAtWrite = fakes.Persons.Query().Count();
                googleUsersAtWrite = fakes.GoogleUsers.Query().Count();
                applicationsAtWrite = fakes.Applications.Query().Count();
            })
        };
        var before = DateTime.UtcNow;

        // When
        var output = await fakes.Handler().HandleAsync(new HardDeleteScopeCommand { Id = scope.PublicId });

        // Then — exactly the three removed rows are tombstoned, each with its kind, in one write
        Assert.True(output.Success);
        Assert.Equal(1, fakes.ErasedSubjects.Calls);
        var tombstones = fakes.ErasedSubjects.Recorded.ToDictionary(x => x.Id);
        Assert.Equal(3, tombstones.Count);
        Assert.Equal((int)ErasedSubjectKinds.Person, tombstones[first.PublicId].Kind);
        Assert.Equal((int)ErasedSubjectKinds.Person, tombstones[second.PublicId].Kind);
        Assert.Equal((int)ErasedSubjectKinds.GoogleUser, tombstones[googleUser.PublicId].Kind);
        Assert.All(tombstones.Values, x => Assert.InRange(x.CreatedAt, before, DateTime.UtcNow));

        // Then — written while every row was still there: four persons, two Google Users, one application
        Assert.Equal(4, personsAtWrite);
        Assert.Equal(2, googleUsersAtWrite);
        Assert.Equal(1, applicationsAtWrite);

        // Then — the owner and the other scope's members are neither removed nor tombstoned
        Assert.Contains((await fakes.Persons.GetAllAsync()).Data!, p => p.PublicId == owner.PublicId);
        Assert.DoesNotContain(scope.PublicId, tombstones.Keys);
    }

    [UnitFact]
    public async Task GivenScopeWithNoMembers_WhenHandlingHardDeleteScope_ThenNothingIsTombstoned()
    {
        // Given
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        await SeedApplicationAsync(fakes, scope.Id);

        // When
        var output = await fakes.Handler().HandleAsync(new HardDeleteScopeCommand { Id = scope.PublicId });

        // Then — a scope is not a person, so its own id is never tombstoned
        Assert.True(output.Success);
        Assert.Empty(fakes.ErasedSubjects.Recorded);
    }

    [UnitFact]
    public async Task GivenTombstonesFromAFailedAttempt_WhenHandlingHardDeleteScopeAgain_ThenItSucceedsWithOneTombstoneEach()
    {
        // Given a scope whose earlier hard delete tombstoned its member and then failed
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        var member = await SeedUserAsync(fakes, scope.Id);
        var googleUser = await SeedGoogleUserAsync(fakes, scope.Id);
        var earlier = DateTime.UtcNow.AddDays(-3);
        fakes.ErasedSubjects.Seed(member.PublicId, (int)ErasedSubjectKinds.Person, earlier);

        // When
        var output = await fakes.Handler().HandleAsync(new HardDeleteScopeCommand { Id = scope.PublicId });

        // Then — the retry is not an error: one row per id, the earlier one's grace counted from now
        Assert.True(output.Success);
        Assert.Equal(2, fakes.ErasedSubjects.Recorded.Count);
        Assert.True(fakes.ErasedSubjects.Recorded.Single(x => x.Id == member.PublicId).CreatedAt > earlier.AddDays(1));
        Assert.Contains(fakes.ErasedSubjects.Recorded, x => x.Id == googleUser.PublicId);
        Assert.Empty((await fakes.Persons.GetAllAsync()).Data!);
    }
}
