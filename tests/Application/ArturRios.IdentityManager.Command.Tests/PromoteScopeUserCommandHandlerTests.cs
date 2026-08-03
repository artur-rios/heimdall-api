using ArturRios.IdentityManager.Command.Handlers;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for PromoteScopeUserCommandHandler (UC-23): the main flow moving a person from
// SCOPE_USER to SCOPE_OWNER for both actors, AF-23a (scope missing or logically deleted), AF-23b
// (person missing, deleted, or not a User of this scope), AF-23c delegation (the checker rejects the
// actor), AF-23d (already a ScopeAdmin), and the FR-PE-09 email-namespace guard. The AF-23c ownership
// rule itself is covered by ScopeOwnershipCheckerTests; the 401/403-by-attribute flows are covered by
// PersonControllerPromoteScopeUserTests.
public class PromoteScopeUserCommandHandlerTests
{
    private static IScopeOwnershipChecker OwnershipChecker(bool allowed = true)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(c => c.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);
        return checker.Object;
    }

    private static async Task<Scope> SeedScopeAsync(
        AsyncFakeRepository<Scope> scopes, string name = "Acme", bool isDeleted = false)
    {
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = name, IsDeleted = isDeleted };
        await scopes.CreateAsync(scope);
        return scope;
    }

    private static async Task<Person> SeedUserAsync(
        AsyncFakeRepository<Person> persons, Scope scope, bool isDeleted = false, string? email = null)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Member",
            Email = email ?? $"user-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.User,
            IsDeleted = isDeleted,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow.AddDays(-1),
            ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope }
        };

        await persons.CreateAsync(person);
        return person;
    }

    private static async Task<Person> SeedAdminAsync(
        AsyncFakeRepository<Person> persons,
        Roles role = Roles.ScopeAdmin,
        bool isDeleted = false,
        string? email = null,
        params Scope[] ownedScopes)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Admin",
            Email = email ?? $"admin-{Guid.NewGuid():N}@test.local",
            RoleId = (long)role,
            IsDeleted = isDeleted,
            ScopeOwnerships = [.. ownedScopes.Select(scope => new ScopeOwner { ScopeId = scope.Id })]
        };

        await persons.CreateAsync(person);
        return person;
    }

    private static PromoteScopeUserCommand Command(
        Guid scopeId, Guid personId, Roles actingRole = Roles.SystemAdmin, Guid? actingPersonId = null) => new()
    {
        ScopeId = scopeId,
        PersonId = personId,
        ActingRole = (int)actingRole,
        ActingPersonId = actingPersonId ?? Guid.NewGuid()
    };

    private static PromoteScopeUserCommandHandler Handler(
        AsyncFakeRepository<Scope> scopes, AsyncFakeRepository<Person> persons, bool allowed = true) =>
        new(scopes, persons, persons, OwnershipChecker(allowed));

    private static async Task<Person> StoredAsync(AsyncFakeRepository<Person> persons, Person person) =>
        (await persons.GetAllAsync()).Data!.Single(x => x.PublicId == person.PublicId);

    [UnitFact]
    public async Task GivenSystemAdminAndScopeUser_WhenHandlingPromoteScopeUser_ThenPersonBecomesScopeOwner()
    {
        // Given a scope and a User belonging to it (UC-23 main flow)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedUserAsync(persons, scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then — output
        Assert.True(output.Success);
        Assert.Equal(person.PublicId, output.Data!.Id);
        Assert.Equal((int)Roles.ScopeAdmin, output.Data.Role);
        Assert.Equal(scope.PublicId, Assert.Single(output.Data.OwnedScopeIds));
        Assert.Contains(PersonMessages.ScopeUserPromotedSuccessfully, output.Messages);

        // Then — the person is a ScopeAdmin owning the scope, no longer a member of it
        var stored = await StoredAsync(persons, person);
        Assert.Equal((long)Roles.ScopeAdmin, stored.RoleId);
        Assert.Null(stored.ScopeMembership);
        Assert.Equal(scope.Id, Assert.Single(stored.ScopeOwnerships).ScopeId);
    }

    [UnitFact]
    public async Task GivenExistingOwnerActor_WhenHandlingPromoteScopeUser_ThenPersonBecomesScopeOwner()
    {
        // Given a Scope Admin actor the checker accepts as an owner of the scope (FR-SC-13)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var actor = await SeedAdminAsync(persons, ownedScopes: scope);
        var person = await SeedUserAsync(persons, scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin, actor.PublicId));

        // Then — the scope now has both owners and the promoted person kept nothing of their membership
        Assert.True(output.Success);
        Assert.Contains(PersonMessages.ScopeUserPromotedSuccessfully, output.Messages);
        var stored = await StoredAsync(persons, person);
        Assert.Equal((long)Roles.ScopeAdmin, stored.RoleId);
        Assert.Equal(scope.Id, Assert.Single(stored.ScopeOwnerships).ScopeId);
        Assert.Equal(scope.Id, Assert.Single((await StoredAsync(persons, actor)).ScopeOwnerships).ScopeId);
    }

    [UnitFact]
    public async Task GivenScopeUser_WhenHandlingPromoteScopeUser_ThenMembershipIsRemovedAndUpdatedAtIsStamped()
    {
        // Given a User whose record was last touched a day ago (UC-23 steps 4-5)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedUserAsync(persons, scope);
        var before = person.UpdatedAt;
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then — the SCOPE_USER row is severed and the timestamp moved; no trigger maintains it
        Assert.True(output.Success);
        var stored = await StoredAsync(persons, person);
        Assert.Null(stored.ScopeMembership);
        Assert.True(stored.UpdatedAt > before);
        Assert.Equal(stored.UpdatedAt, output.Data!.UpdatedAt);
    }

    [UnitFact]
    public async Task GivenOutput_WhenHandlingPromoteScopeUser_ThenItCarriesPublicIdentifiersOnly()
    {
        // Given a User with a known name and email (SRD §4.0, NFR-15)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedUserAsync(persons, scope, email: "member@test.local");
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then — every identifier is a PublicId and the credentials are absent by construction
        Assert.Equal(person.PublicId, output.Data!.Id);
        Assert.Equal("Member", output.Data.Name);
        Assert.Equal("member@test.local", output.Data.Email);
        Assert.Equal(person.CreatedAt, output.Data.CreatedAt);
        Assert.Equal(scope.PublicId, Assert.Single(output.Data.OwnedScopeIds));
    }

    [UnitFact]
    public async Task GivenUnknownScope_WhenHandlingPromoteScopeUser_ThenScopeNotFoundIsReported()
    {
        // Given a scope id nothing matches (AF-23a)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedUserAsync(persons, scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(Guid.NewGuid(), person.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
        var stored = await StoredAsync(persons, person);
        Assert.Equal((long)Roles.User, stored.RoleId);
        Assert.NotNull(stored.ScopeMembership);
        Assert.Empty(stored.ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedScope_WhenHandlingPromoteScopeUser_ThenScopeNotFoundIsReported()
    {
        // Given a scope withdrawn from service (AF-23a treats it as absent)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes, isDeleted: true);
        var person = await SeedUserAsync(persons, scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
        var stored = await StoredAsync(persons, person);
        Assert.Equal((long)Roles.User, stored.RoleId);
        Assert.NotNull(stored.ScopeMembership);
    }

    [UnitFact]
    public async Task GivenUnknownPerson_WhenHandlingPromoteScopeUser_ThenPersonNotScopeUserIsReported()
    {
        // Given a person id nothing matches (AF-23b)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotScopeUser, output.Errors);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedUser_WhenHandlingPromoteScopeUser_ThenPersonNotScopeUserIsReported()
    {
        // Given a logically deleted User — they can no longer authenticate, so the ownership would be
        // unusable (AF-23b)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedUserAsync(persons, scope, isDeleted: true);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotScopeUser, output.Errors);
        var stored = await StoredAsync(persons, person);
        Assert.Equal((long)Roles.User, stored.RoleId);
        Assert.NotNull(stored.ScopeMembership);
        Assert.Empty(stored.ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenUserOfAnotherScope_WhenHandlingPromoteScopeUser_ThenPersonNotScopeUserIsReported()
    {
        // Given a User who belongs to some other scope (AF-23b)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes, "Other");
        var person = await SeedUserAsync(persons, otherScope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotScopeUser, output.Errors);
        var stored = await StoredAsync(persons, person);
        Assert.Equal((long)Roles.User, stored.RoleId);
        Assert.Equal(otherScope.Id, stored.ScopeMembership!.ScopeId);
        Assert.Empty(stored.ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenSystemAdminPerson_WhenHandlingPromoteScopeUser_ThenPersonNotScopeUserIsReported()
    {
        // Given a System Admin target — not a User of any scope, and AF-23d covers only ScopeAdmins
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedAdminAsync(persons, Roles.SystemAdmin);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotScopeUser, output.Errors);
        Assert.Equal((long)Roles.SystemAdmin, (await StoredAsync(persons, person)).RoleId);
    }

    [UnitFact]
    public async Task GivenScopeAdminNotOwningTheScope_WhenHandlingPromoteScopeUser_ThenNotScopeOwnerIsReported()
    {
        // Given an actor the ownership checker rejects (AF-23c)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedUserAsync(persons, scope);
        var handler = Handler(scopes, persons, allowed: false);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotScopeOwner, output.Errors);
        var stored = await StoredAsync(persons, person);
        Assert.Equal((long)Roles.User, stored.RoleId);
        Assert.NotNull(stored.ScopeMembership);
        Assert.Empty(stored.ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenPersonAlreadyScopeAdmin_WhenHandlingPromoteScopeUser_ThenAlreadyScopeAdminIsReported()
    {
        // Given a target who already holds the role the promotion would grant (AF-23d)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedAdminAsync(persons, ownedScopes: scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then — 409, and the ownership row they already had is untouched
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.AlreadyScopeAdmin, output.Errors);
        var stored = await StoredAsync(persons, person);
        Assert.Equal((long)Roles.ScopeAdmin, stored.RoleId);
        Assert.Equal(scope.Id, Assert.Single(stored.ScopeOwnerships).ScopeId);
    }

    [UnitFact]
    public async Task GivenUnauthorizedActorAndUnknownPerson_WhenHandlingPromoteScopeUser_ThenNotScopeOwnerIsReported()
    {
        // Given an actor the checker rejects, naming a person who does not exist. The authorization
        // answer must not depend on data the caller is not allowed to learn (design Decision 2).
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var handler = Handler(scopes, persons, allowed: false);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, Guid.NewGuid(), Roles.ScopeAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotScopeOwner, output.Errors);
        Assert.DoesNotContain(PersonMessages.PersonNotScopeUser, output.Errors);
    }

    [UnitFact]
    public async Task GivenEmailAlreadyUsedByAnAdmin_WhenHandlingPromoteScopeUser_ThenEmailAlreadyExistsIsReported()
    {
        // Given a User whose address already belongs to a ScopeAdmin. Promotion would move it into
        // the admin namespace, where FR-PE-09 requires it to be unique system-wide.
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        await SeedAdminAsync(persons, email: "Shared@test.local");
        var person = await SeedUserAsync(persons, scope, email: "shared@test.local");
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then — refused case-insensitively, and nothing was written
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
        var stored = await StoredAsync(persons, person);
        Assert.Equal((long)Roles.User, stored.RoleId);
        Assert.NotNull(stored.ScopeMembership);
        Assert.Empty(stored.ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenEmailUsedOnlyByAUserOfAnotherScope_WhenHandlingPromoteScopeUser_ThenPersonBecomesScopeOwner()
    {
        // Given the same address held by a User of another scope. FR-PE-09 keeps the two namespaces
        // independent, so the admin-side check must not see it.
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes, "Other");
        await SeedUserAsync(persons, otherScope, email: "shared@test.local");
        var person = await SeedUserAsync(persons, scope, email: "shared@test.local");
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Contains(PersonMessages.ScopeUserPromotedSuccessfully, output.Messages);
        Assert.Equal((long)Roles.ScopeAdmin, (await StoredAsync(persons, person)).RoleId);
    }

    [UnitFact]
    public async Task GivenDeletedAdminHoldingTheEmail_WhenHandlingPromoteScopeUser_ThenPersonBecomesScopeOwner()
    {
        // Given the colliding admin is logically deleted — they hold no live claim on the address,
        // the same exclusion UC-06 path c applies (FR-PE-09).
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        await SeedAdminAsync(persons, isDeleted: true, email: "shared@test.local");
        var person = await SeedUserAsync(persons, scope, email: "shared@test.local");
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Equal((long)Roles.ScopeAdmin, (await StoredAsync(persons, person)).RoleId);
    }
}
