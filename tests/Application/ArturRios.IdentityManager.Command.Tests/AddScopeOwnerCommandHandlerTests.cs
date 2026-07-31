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

// Unit tests for AddScopeOwnerCommandHandler (UC-21): the main flow inserting a SCOPE_OWNER row for
// both actors, AF-21a (scope missing or logically deleted), AF-21b (person missing, deleted, or not a
// ScopeAdmin), AF-21c delegation (the checker rejects the actor), and AF-21d (already an owner). The
// AF-21c ownership rule itself is covered by ScopeOwnershipCheckerTests; the 401/403-by-attribute
// flows are covered by PersonControllerAddScopeOwnerTests.
public class AddScopeOwnerCommandHandlerTests
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

    private static AddScopeOwnerCommand Command(
        Guid scopeId, Guid personId, Roles actingRole = Roles.SystemAdmin, Guid? actingPersonId = null) => new()
    {
        ScopeId = scopeId,
        PersonId = personId,
        ActingRole = (int)actingRole,
        ActingPersonId = actingPersonId ?? Guid.NewGuid()
    };

    private static AddScopeOwnerCommandHandler Handler(
        AsyncFakeRepository<Scope> scopes, AsyncFakeRepository<Person> persons, bool allowed = true) =>
        new(scopes, persons, persons, OwnershipChecker(allowed));

    private static async Task<Person> StoredAsync(AsyncFakeRepository<Person> persons, Person person) =>
        (await persons.GetAllAsync()).Data!.Single(x => x.PublicId == person.PublicId);

    [UnitFact]
    public async Task GivenSystemAdminAndScopeAdminPerson_WhenHandlingAddScopeOwner_ThenOwnershipIsAdded()
    {
        // Given a scope and a ScopeAdmin who does not own it yet (UC-21 main flow)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then — output
        Assert.True(output.Success);
        Assert.Equal(scope.PublicId, output.Data!.ScopeId);
        Assert.Equal(person.PublicId, output.Data.PersonId);
        Assert.False(output.Data.AlreadyOwner);
        Assert.Contains(PersonMessages.ScopeOwnerAddedSuccessfully, output.Messages);

        // Then — the SCOPE_OWNER row exists
        var stored = await StoredAsync(persons, person);
        Assert.Equal(scope.Id, Assert.Single(stored.ScopeOwnerships).ScopeId);
    }

    [UnitFact]
    public async Task GivenExistingOwnerActor_WhenHandlingAddScopeOwner_ThenOwnershipIsAdded()
    {
        // Given a Scope Admin actor the checker accepts as an owner of the scope (FR-SC-09)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var actor = await SeedPersonAsync(persons, ownedScopes: scope);
        var person = await SeedPersonAsync(persons);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin, actor.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Contains(PersonMessages.ScopeOwnerAddedSuccessfully, output.Messages);
        var stored = await StoredAsync(persons, person);
        Assert.Equal(scope.Id, Assert.Single(stored.ScopeOwnerships).ScopeId);
    }

    [UnitFact]
    public async Task GivenPersonOwningAnotherScope_WhenHandlingAddScopeOwner_ThenExistingOwnershipsSurvive()
    {
        // Given a ScopeAdmin who already owns a different scope — FR-SC-08 lets a person own several
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes, "Globex");
        var person = await SeedPersonAsync(persons, ownedScopes: otherScope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then — both ownerships are present
        Assert.True(output.Success);
        var stored = await StoredAsync(persons, person);
        Assert.Equal(2, stored.ScopeOwnerships.Count);
        Assert.Contains(stored.ScopeOwnerships, ownership => ownership.ScopeId == scope.Id);
        Assert.Contains(stored.ScopeOwnerships, ownership => ownership.ScopeId == otherScope.Id);
    }

    [UnitFact]
    public async Task GivenOutput_WhenHandlingAddScopeOwner_ThenItCarriesPublicIdentifiersOnly()
    {
        // Given internal Ids that differ from the public ones, so a leak would be visible
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons);
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
    public async Task GivenUnknownScope_WhenHandlingAddScopeOwner_ThenScopeNotFoundIsReported()
    {
        // Given no scope with the requested id (AF-21a)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var person = await SeedPersonAsync(persons);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(Guid.NewGuid(), person.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
        Assert.Empty((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedScope_WhenHandlingAddScopeOwner_ThenScopeNotFoundIsReported()
    {
        // Given a logically deleted scope — AF-21a treats it as absent
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes, isDeleted: true);
        var person = await SeedPersonAsync(persons);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
        Assert.Empty((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenUnknownPerson_WhenHandlingAddScopeOwner_ThenPersonNotValidScopeAdminIsReported()
    {
        // Given no person with the requested id (AF-21b)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotValidScopeAdmin, output.Errors);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedPerson_WhenHandlingAddScopeOwner_ThenPersonNotValidScopeAdminIsReported()
    {
        // Given a logically deleted ScopeAdmin — they can no longer authenticate (AF-21b)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons, isDeleted: true);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotValidScopeAdmin, output.Errors);
        Assert.Empty((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitTheory]
    [InlineData(Roles.User)]
    [InlineData(Roles.SystemAdmin)]
    public async Task GivenPersonWithoutScopeAdminRole_WhenHandlingAddScopeOwner_ThenPersonNotValidScopeAdminIsReported(
        Roles role)
    {
        // Given a person who is not a ScopeAdmin — only a ScopeAdmin may own a scope (FR-SC-08, AF-21b)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons, role);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotValidScopeAdmin, output.Errors);
        Assert.Empty((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenScopeAdminNotOwningTheScope_WhenHandlingAddScopeOwner_ThenNotScopeOwnerIsReported()
    {
        // Given the ownership checker rejects the actor (AF-21c)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons);
        var handler = Handler(scopes, persons, allowed: false);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotScopeOwner, output.Errors);
        Assert.Empty((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenPersonAlreadyOwner_WhenHandlingAddScopeOwner_ThenAlreadyOwnerIsReportedAndNoRowIsAdded()
    {
        // Given a ScopeAdmin who already owns the scope (AF-21d)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons, ownedScopes: scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then — success, flagged as the no-op, and still exactly one ownership row
        Assert.True(output.Success);
        Assert.True(output.Data!.AlreadyOwner);
        Assert.Contains(PersonMessages.AlreadyScopeOwner, output.Messages);
        Assert.DoesNotContain(PersonMessages.ScopeOwnerAddedSuccessfully, output.Messages);
        Assert.Equal(scope.Id, Assert.Single((await StoredAsync(persons, person)).ScopeOwnerships).ScopeId);
    }

    [UnitFact]
    public async Task GivenUnauthorizedActorAndUnknownPerson_WhenHandlingAddScopeOwner_ThenNotScopeOwnerIsReported()
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
        Assert.DoesNotContain(PersonMessages.PersonNotValidScopeAdmin, output.Errors);
    }
}
