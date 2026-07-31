using ArturRios.IdentityManager.Command.Handlers;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Application = ArturRios.IdentityManager.Domain.Entities.Application;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for DeleteApplicationCommandHandler (UC-19): the main flow for a System Admin and for
// the owning Scope Admin, AF-19a (application missing, in another scope, or addressed through an
// unknown scope), AF-19b (already deleted — an idempotent success that writes nothing), and AF-19c
// (an actor who does not own the application), including AF-19c taking priority over AF-19b for a
// non-owner (design Decision 3). A `User` never reaches the handler — [RoleRequirement] refuses them
// at the endpoint, covered in ApplicationControllerDeleteTests.
public class DeleteApplicationCommandHandlerTests
{
    // A fixed, obviously-not-now timestamp, so "UpdatedAt was (not) stamped" is a meaningful assertion.
    private static readonly DateTime Stamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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
            Owner = owner,
            UpdatedAt = Stamp
        };
        await applications.CreateAsync(application);
        return application;
    }

    private static DeleteApplicationCommand Command(
        Guid scopeId, Guid id, int actingRole, Guid actingPersonId) => new()
    {
        ScopeId = scopeId,
        Id = id,
        ActingRole = actingRole,
        ActingPersonId = actingPersonId
    };

    private static DeleteApplicationCommandHandler Handler(AsyncFakeRepository<Application> applications) =>
        new(applications, applications);

    private static async Task<Application> StoredAsync(AsyncFakeRepository<Application> applications, Guid publicId) =>
        (await applications.GetAllAsync()).Data!.Single(a => a.PublicId == publicId);

    [UnitFact]
    public async Task GivenSystemAdmin_WhenHandlingDeleteApplication_ThenApplicationIsLogicallyDeleted()
    {
        // Given an application a System Admin does not own (UC-19 step 2)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);
        Assert.Equal(application.PublicId, output.Data!.Id);
        Assert.False(output.Data.AlreadyDeleted);
        Assert.Contains(ApplicationMessages.ApplicationDeletedSuccessfully, output.Messages);
        Assert.True((await StoredAsync(applications, application.PublicId)).IsDeleted);
    }

    [UnitFact]
    public async Task GivenOwningScopeAdmin_WhenHandlingDeleteApplication_ThenApplicationIsLogicallyDeleted()
    {
        // Given the ScopeAdmin who owns the application deleting it (UC-19 step 2)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, (int)Roles.ScopeAdmin, owner.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.False(output.Data!.AlreadyDeleted);
        Assert.True((await StoredAsync(applications, application.PublicId)).IsDeleted);
    }

    [UnitFact]
    public async Task GivenActiveApplication_WhenHandlingDeleteApplication_ThenUpdatedAtIsStampedAndCreatedAtIsNot()
    {
        // Given an existing application (UC-19 step 3: no DB trigger maintains UpdatedAt)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var createdAt = application.CreatedAt;
        var before = DateTime.UtcNow;
        var handler = Handler(applications);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);

        var stored = await StoredAsync(applications, application.PublicId);
        Assert.True(stored.UpdatedAt >= before);
        Assert.Equal(createdAt, stored.CreatedAt);
    }

    [UnitFact]
    public async Task GivenOutput_WhenHandlingDeleteApplication_ThenItCarriesPublicIdentifiersOnly()
    {
        // Given internal ids that must never leave the data layer (SRD §4.0)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then — the only identifier on the output is the application's PublicId
        Assert.Equal(application.PublicId, output.Data!.Id);
        Assert.NotEqual(Guid.Empty, output.Data.Id);
    }

    [UnitFact]
    public async Task GivenAlreadyDeletedApplication_WhenHandlingDeleteApplication_ThenSuccessReportsAlreadyDeleted()
    {
        // Given an application that is already logically deleted (AF-19b: idempotent)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner, isDeleted: true);
        var handler = Handler(applications);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then — the same success as the main flow, distinguished only by the flag
        Assert.True(output.Success);
        Assert.Equal(application.PublicId, output.Data!.Id);
        Assert.True(output.Data.AlreadyDeleted);
        Assert.Contains(ApplicationMessages.ApplicationDeletedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenAlreadyDeletedApplication_WhenHandlingDeleteApplication_ThenNothingIsWritten()
    {
        // Given an already-deleted application: the row carries the state the request asks for, so
        // re-stamping UpdatedAt would misreport when the deletion happened
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner, isDeleted: true);
        var handler = Handler(applications);

        // When
        await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        var stored = await StoredAsync(applications, application.PublicId);
        Assert.True(stored.IsDeleted);
        Assert.Equal(Stamp, stored.UpdatedAt);
    }

    [UnitFact]
    public async Task GivenUnknownApplication_WhenHandlingDeleteApplication_ThenApplicationNotFoundIsReported()
    {
        // Given an application id nobody holds (AF-19a)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.ApplicationNotFound, output.Errors);
        Assert.False((await StoredAsync(applications, application.PublicId)).IsDeleted);
    }

    [UnitFact]
    public async Task GivenApplicationOfADifferentScope_WhenHandlingDeleteApplication_ThenApplicationNotFoundIsReported()
    {
        // Given an application that exists, but under a different scope than the path addresses: the
        // lookup is qualified by the route's scopeId (AF-19a)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes, "Other");
        var owner = await SeedScopeAdminAsync(persons, ownedScope: otherScope);
        var application = await SeedApplicationAsync(applications, otherScope, owner);
        var handler = Handler(applications);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.ApplicationNotFound, output.Errors);
        Assert.False((await StoredAsync(applications, application.PublicId)).IsDeleted);
    }

    [UnitFact]
    public async Task GivenUnknownScope_WhenHandlingDeleteApplication_ThenApplicationNotFoundIsReported()
    {
        // Given a scope id nobody holds: an unknown scope is the same one 404 (AF-19a)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications);

        // When
        var output = await handler.HandleAsync(Command(
            Guid.NewGuid(), application.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.ApplicationNotFound, output.Errors);
        Assert.False((await StoredAsync(applications, application.PublicId)).IsDeleted);
    }

    [UnitFact]
    public async Task
        GivenScopeAdminWhoOwnsTheScopeButNotTheApplication_WhenHandlingDeleteApplication_ThenNotAuthorizedIsReported()
    {
        // Given a co-owner of the scope: owning the scope is not grounds to delete another owner's
        // application (AF-19c)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var coOwner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, (int)Roles.ScopeAdmin, coOwner.PublicId));

        // Then — refused, and the row is still active
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.NotAuthorizedToDeleteApplication, output.Errors);

        var stored = await StoredAsync(applications, application.PublicId);
        Assert.False(stored.IsDeleted);
        Assert.Equal(Stamp, stored.UpdatedAt);
    }

    [UnitFact]
    public async Task GivenUnrelatedScopeAdmin_WhenHandlingDeleteApplication_ThenNotAuthorizedIsReported()
    {
        // Given a ScopeAdmin with no tie to the application's scope at all (AF-19c)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes, "Other");
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var stranger = await SeedScopeAdminAsync(persons, ownedScope: otherScope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, (int)Roles.ScopeAdmin, stranger.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.NotAuthorizedToDeleteApplication, output.Errors);
        Assert.False((await StoredAsync(applications, application.PublicId)).IsDeleted);
    }

    [UnitFact]
    public async Task GivenNonOwnerAndAlreadyDeletedApplication_WhenHandlingDeleteApplication_ThenNotAuthorizedIsReported()
    {
        // Given a non-owner addressing an already-deleted application: authorization runs before the
        // AF-19b no-op, so the idempotent success cannot be used to probe applications the caller may
        // not see (design Decision 3)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var coOwner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner, isDeleted: true);
        var handler = Handler(applications);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, (int)Roles.ScopeAdmin, coOwner.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.NotAuthorizedToDeleteApplication, output.Errors);
    }
}
