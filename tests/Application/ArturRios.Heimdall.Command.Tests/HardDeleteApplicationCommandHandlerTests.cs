using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for HardDeleteApplicationCommandHandler (UC-20): the main flow removing the record for
// good, the same treatment for an already logically deleted application (design Decision 1), and
// AF-20a (application missing, in another scope, addressed through an unknown scope, or already hard
// deleted by an earlier call — Decision 6). Authorization is entirely the endpoint's: UC-20's only
// actor is the System Admin, the command carries no acting person (Decision 2), and the 403/401 flows
// are covered in ApplicationControllerHardDeleteTests.
public class HardDeleteApplicationCommandHandlerTests
{
    private static async Task<Scope> SeedScopeAsync(AsyncFakeRepository<Scope> scopes, string name = "Acme")
    {
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = name };
        await scopes.CreateAsync(scope);
        return scope;
    }

    private static async Task<Person> SeedScopeAdminAsync(AsyncFakeRepository<Person> persons, Scope? ownedScope = null)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Admin",
            Email = $"admin-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.ScopeAdmin
        };

        if (ownedScope is not null)
        {
            person.ScopeOwnerships = [new ScopeOwner { ScopeId = ownedScope.Id }];
        }

        await persons.CreateAsync(person);
        return person;
    }

    private static async Task<Application> SeedApplicationAsync(
        AsyncFakeRepository<Application> applications, Scope scope, Person owner, bool isDeleted = false)
    {
        var application = new Application
        {
            PublicId = Guid.NewGuid(),
            Name = "Billing Service",
            IsDeleted = isDeleted,
            ScopeId = scope.Id,
            Scope = scope,
            OwnerId = owner.Id,
            Owner = owner
        };
        await applications.CreateAsync(application);
        return application;
    }

    private static HardDeleteApplicationCommand Command(Guid scopeId, Guid id) => new()
    {
        ScopeId = scopeId,
        Id = id
    };

    private static HardDeleteApplicationCommandHandler Handler(AsyncFakeRepository<Application> applications) =>
        new(applications, applications);

    private static async Task<IEnumerable<Application>> StoredAsync(AsyncFakeRepository<Application> applications) =>
        (await applications.GetAllAsync()).Data!;

    [UnitFact]
    public async Task GivenSystemAdmin_WhenHandlingHardDeleteApplication_ThenApplicationIsRemoved()
    {
        // Given an active application (UC-20 main flow)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);

        // When
        var output = await Handler(applications).HandleAsync(Command(scope.PublicId, application.PublicId));

        // Then — the response reports the application, and the record is gone for good
        Assert.True(output.Success);
        Assert.Equal(application.PublicId, output.Data!.Id);
        Assert.Contains(ApplicationMessages.ApplicationHardDeletedSuccessfully, output.Messages);
        Assert.Empty(await StoredAsync(applications));
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedApplication_WhenHandlingHardDeleteApplication_ThenApplicationIsRemoved()
    {
        // Given an application already carrying IsDeleted — exactly what a cleanup pass starts from,
        // so the lookup omits the !IsDeleted filter (Decision 1)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner, isDeleted: true);

        // When
        var output = await Handler(applications).HandleAsync(Command(scope.PublicId, application.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Equal(application.PublicId, output.Data!.Id);
        Assert.Empty(await StoredAsync(applications));
    }

    [UnitFact]
    public async Task GivenOutput_WhenHandlingHardDeleteApplication_ThenItCarriesPublicIdentifiersOnly()
    {
        // Given internal ids that must never leave the data layer (SRD §4.0, Decision 8)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);

        // When
        var output = await Handler(applications).HandleAsync(Command(scope.PublicId, application.PublicId));

        // Then — the only identifier on the output is the application's PublicId
        Assert.Equal(application.PublicId, output.Data!.Id);
        Assert.NotEqual(Guid.Empty, output.Data.Id);
    }

    [UnitFact]
    public async Task GivenSiblingApplicationInTheSameScope_WhenHandlingHardDeleteApplication_ThenOnlyTheAddressedOneIsRemoved()
    {
        // Given two applications of the same scope, only one of them addressed
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var target = await SeedApplicationAsync(applications, scope, owner);
        var sibling = await SeedApplicationAsync(applications, scope, owner);

        // When
        var output = await Handler(applications).HandleAsync(Command(scope.PublicId, target.PublicId));

        // Then — the sibling survives untouched
        Assert.True(output.Success);
        var stored = (await StoredAsync(applications)).ToList();
        Assert.Single(stored);
        Assert.Equal(sibling.PublicId, stored[0].PublicId);
    }

    [UnitFact]
    public async Task GivenUnknownApplication_WhenHandlingHardDeleteApplication_ThenApplicationNotFoundIsReported()
    {
        // Given an application id nobody holds (AF-20a)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        await SeedApplicationAsync(applications, scope, owner);

        // When
        var output = await Handler(applications).HandleAsync(Command(scope.PublicId, Guid.NewGuid()));

        // Then — refused, and the existing application is untouched
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.ApplicationNotFound, output.Errors);
        Assert.Single(await StoredAsync(applications));
    }

    [UnitFact]
    public async Task GivenApplicationOfADifferentScope_WhenHandlingHardDeleteApplication_ThenApplicationNotFoundIsReported()
    {
        // Given the application exists, but under a different scope than the command addresses
        // (AF-20a, Decision 4)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes, "Other");
        var owner = await SeedScopeAdminAsync(persons, ownedScope: otherScope);
        var application = await SeedApplicationAsync(applications, otherScope, owner);

        // When
        var output = await Handler(applications).HandleAsync(Command(scope.PublicId, application.PublicId));

        // Then — refused, and the row survives
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.ApplicationNotFound, output.Errors);
        Assert.Single(await StoredAsync(applications));
    }

    [UnitFact]
    public async Task GivenUnknownScope_WhenHandlingHardDeleteApplication_ThenApplicationNotFoundIsReported()
    {
        // Given a scope id nobody holds — an unknown scope and an unknown application are one 404
        // (AF-20a, Decision 4)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);

        // When
        var output = await Handler(applications).HandleAsync(Command(Guid.NewGuid(), application.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.ApplicationNotFound, output.Errors);
        Assert.Single(await StoredAsync(applications));
    }

    [UnitFact]
    public async Task GivenAlreadyHardDeletedApplication_WhenHandlingHardDeleteApplication_ThenApplicationNotFoundIsReported()
    {
        // Given the same application hard deleted twice: the row is gone, so the second call has
        // nothing to find. UC-20 has no idempotent path — unlike UC-19's AF-19b (Decision 6)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications);

        // When
        var first = await handler.HandleAsync(Command(scope.PublicId, application.PublicId));
        var second = await handler.HandleAsync(Command(scope.PublicId, application.PublicId));

        // Then
        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Contains(ApplicationMessages.ApplicationNotFound, second.Errors);
    }
}
