using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for RemoveScopeOwnerCommandHandler (UC-22): the main flow removing a SCOPE_OWNER row for
// both actors, AF-22a (scope missing or logically deleted; person unknown or not an owner of this
// scope), AF-22b (the scope would be left without a live owner, NFR-12), and AF-22c delegation (the
// checker rejects the actor). The AF-22c ownership rule itself is covered by ScopeOwnershipCheckerTests;
// the 401/403-by-attribute flows are covered by PersonControllerRemoveScopeOwnerTests.
public class RemoveScopeOwnerCommandHandlerTests
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

    private static async Task<Person> SeedPersonAsync(
        AsyncFakeRepository<Person> persons,
        Roles role = Roles.ScopeAdmin,
        bool isDeleted = false,
        params Scope[] ownedScopes)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Admin",
            Email = $"admin-{Guid.NewGuid():N}@test.local",
            RoleId = (long)role,
            IsDeleted = isDeleted,
            ScopeOwnerships = [.. ownedScopes.Select(scope => new ScopeOwner { ScopeId = scope.Id })]
        };

        await persons.CreateAsync(person);
        return person;
    }

    private static RemoveScopeOwnerCommand Command(
        Guid scopeId, Guid personId, Roles actingRole = Roles.SystemAdmin, Guid? actingPersonId = null) => new()
    {
        ScopeId = scopeId,
        PersonId = personId,
        ActingRole = (int)actingRole,
        ActingPersonId = actingPersonId ?? Guid.NewGuid()
    };

    private static RemoveScopeOwnerCommandHandler Handler(
        AsyncFakeRepository<Scope> scopes, AsyncFakeRepository<Person> persons, bool allowed = true) =>
        new(scopes, persons, persons, OwnershipChecker(allowed));

    private static async Task<Person> StoredAsync(AsyncFakeRepository<Person> persons, Person person) =>
        (await persons.GetAllAsync()).Data!.Single(x => x.PublicId == person.PublicId);

    [UnitFact]
    public async Task GivenSystemAdminAndCoOwnedScope_WhenHandlingRemoveScopeOwner_ThenOwnershipIsRemoved()
    {
        // Given a scope with two owners (UC-22 main flow)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons, ownedScopes: scope);
        var coOwner = await SeedPersonAsync(persons, ownedScopes: scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then — output
        Assert.True(output.Success);
        Assert.Equal(scope.PublicId, output.Data!.ScopeId);
        Assert.Equal(person.PublicId, output.Data.PersonId);
        Assert.Contains(PersonMessages.ScopeOwnerRemovedSuccessfully, output.Messages);

        // Then — the SCOPE_OWNER row is gone and the co-owner's survives
        Assert.Empty((await StoredAsync(persons, person)).ScopeOwnerships);
        Assert.Equal(scope.Id, Assert.Single((await StoredAsync(persons, coOwner)).ScopeOwnerships).ScopeId);
    }

    [UnitFact]
    public async Task GivenCoOwnerActor_WhenHandlingRemoveScopeOwner_ThenOwnershipIsRemoved()
    {
        // Given a Scope Admin actor the checker accepts as an owner of the scope (FR-SC-10)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons, ownedScopes: scope);
        var actor = await SeedPersonAsync(persons, ownedScopes: scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin, actor.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Contains(PersonMessages.ScopeOwnerRemovedSuccessfully, output.Messages);
        Assert.Empty((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenPersonOwningAnotherScope_WhenHandlingRemoveScopeOwner_ThenOtherOwnershipsSurvive()
    {
        // Given a ScopeAdmin who owns two scopes — only the named one is removed (FR-SC-08)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes, "Globex");
        var person = await SeedPersonAsync(persons, ownedScopes: [scope, otherScope]);
        await SeedPersonAsync(persons, ownedScopes: scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then — only the other scope's ownership remains
        Assert.True(output.Success);
        var stored = await StoredAsync(persons, person);
        Assert.Equal(otherScope.Id, Assert.Single(stored.ScopeOwnerships).ScopeId);
    }

    [UnitFact]
    public async Task GivenOutput_WhenHandlingRemoveScopeOwner_ThenItCarriesPublicIdentifiersOnly()
    {
        // Given internal Ids that differ from the public ones, so a leak would be visible
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons, ownedScopes: scope);
        await SeedPersonAsync(persons, ownedScopes: scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then — only PublicIds are reported (SRD §4.0)
        Assert.Equal(scope.PublicId, output.Data!.ScopeId);
        Assert.Equal(person.PublicId, output.Data.PersonId);
        Assert.NotEqual(Guid.Empty, output.Data.ScopeId);
        Assert.NotEqual(Guid.Empty, output.Data.PersonId);
    }

    [UnitFact]
    public async Task GivenUnknownScope_WhenHandlingRemoveScopeOwner_ThenScopeNotFoundIsReported()
    {
        // Given no scope with the requested id (AF-22a)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons, ownedScopes: scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(Guid.NewGuid(), person.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
        Assert.Single((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedScope_WhenHandlingRemoveScopeOwner_ThenScopeNotFoundIsReported()
    {
        // Given a logically deleted scope — treated as absent, as every scope-scoped handler does
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes, isDeleted: true);
        var person = await SeedPersonAsync(persons, ownedScopes: scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
        Assert.Single((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenUnknownPerson_WhenHandlingRemoveScopeOwner_ThenPersonNotScopeOwnerIsReported()
    {
        // Given no person with the requested id (AF-22a)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        await SeedPersonAsync(persons, ownedScopes: scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotScopeOwner, output.Errors);
    }

    [UnitFact]
    public async Task GivenPersonOwningOnlyAnotherScope_WhenHandlingRemoveScopeOwner_ThenPersonNotScopeOwnerIsReported()
    {
        // Given a ScopeAdmin who owns a different scope — no SCOPE_OWNER row links them here (AF-22a)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes, "Globex");
        var person = await SeedPersonAsync(persons, ownedScopes: otherScope);
        await SeedPersonAsync(persons, ownedScopes: scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then — refused, and the unrelated ownership is untouched
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotScopeOwner, output.Errors);
        Assert.Equal(otherScope.Id, Assert.Single((await StoredAsync(persons, person)).ScopeOwnerships).ScopeId);
    }

    [UnitFact]
    public async Task GivenSoleOwner_WhenHandlingRemoveScopeOwner_ThenScopeWouldLoseLastOwnerIsReported()
    {
        // Given the scope's only owner (AF-22b, NFR-12)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons, ownedScopes: scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then — refused and the row survives
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeWouldLoseLastOwner, output.Errors);
        Assert.Equal(scope.Id, Assert.Single((await StoredAsync(persons, person)).ScopeOwnerships).ScopeId);
    }

    [UnitFact]
    public async Task GivenOnlyCoOwnerIsLogicallyDeleted_WhenHandlingRemoveScopeOwner_ThenScopeWouldLoseLastOwnerIsReported()
    {
        // Given the only co-owner is logically deleted — they cannot authenticate (FR-AU-07), so they
        // do not keep the scope owned (AF-22b)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons, ownedScopes: scope);
        await SeedPersonAsync(persons, isDeleted: true, ownedScopes: scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeWouldLoseLastOwner, output.Errors);
        Assert.Single((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedTargetWithLiveCoOwner_WhenHandlingRemoveScopeOwner_ThenStaleOwnershipIsRemoved()
    {
        // Given a logically deleted ScopeAdmin still holding an ownership row — clearing that stale row
        // is exactly what this endpoint is for, and the live co-owner keeps the scope owned
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons, isDeleted: true, ownedScopes: scope);
        await SeedPersonAsync(persons, ownedScopes: scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Contains(PersonMessages.ScopeOwnerRemovedSuccessfully, output.Messages);
        Assert.Empty((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenActorRemovingThemselvesWithCoOwnerRemaining_WhenHandlingRemoveScopeOwner_ThenOwnershipIsRemoved()
    {
        // Given a Scope Admin stepping down from a scope that keeps another owner — nothing in UC-22
        // forbids it, and AF-22b already prevents the damaging case
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var actor = await SeedPersonAsync(persons, ownedScopes: scope);
        await SeedPersonAsync(persons, ownedScopes: scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, actor.PublicId, Roles.ScopeAdmin, actor.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Contains(PersonMessages.ScopeOwnerRemovedSuccessfully, output.Messages);
        Assert.Empty((await StoredAsync(persons, actor)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenScopeAdminNotOwningTheScope_WhenHandlingRemoveScopeOwner_ThenNotScopeOwnerIsReported()
    {
        // Given the ownership checker rejects the actor (AF-22c)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons, ownedScopes: scope);
        await SeedPersonAsync(persons, ownedScopes: scope);
        var handler = Handler(scopes, persons, allowed: false);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin));

        // Then — refused and the row survives
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotScopeOwner, output.Errors);
        Assert.Equal(scope.Id, Assert.Single((await StoredAsync(persons, person)).ScopeOwnerships).ScopeId);
    }

    [UnitFact]
    public async Task GivenUnauthorizedActorAndUnknownPerson_WhenHandlingRemoveScopeOwner_ThenNotScopeOwnerIsReported()
    {
        // Given an actor the checker rejects, naming a person who does not exist. The ownership check
        // runs first (design Decision 2), so the refusal must not reveal anything about the person.
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var handler = Handler(scopes, persons, allowed: false);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, Guid.NewGuid(), Roles.ScopeAdmin));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotScopeOwner, output.Errors);
        Assert.DoesNotContain(PersonMessages.PersonNotScopeOwner, output.Errors);
    }
}
