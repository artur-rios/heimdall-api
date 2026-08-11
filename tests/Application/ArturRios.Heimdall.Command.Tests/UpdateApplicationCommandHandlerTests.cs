using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for UpdateApplicationCommandHandler (UC-18): main flow for a System Admin and for the
// owning Scope Admin, the owner transfer the specification allows (design Decision 1), plus AF-18a
// (application missing, in another scope, or logically deleted), AF-18b (the new owner is not a
// ScopeAdmin owning the application's scope), AF-18c (an actor who does not own the application), and
// step 2's input validation. A `User` never reaches the handler — [RoleRequirement] refuses them at
// the endpoint, covered in ApplicationControllerUpdateTests.
public class UpdateApplicationCommandHandlerTests
{
    private const string NewName = "Billing Service v2";

    private static Mock<IValidator<UpdateApplicationCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<UpdateApplicationCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<UpdateApplicationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator;
    }

    private static async Task<Scope> SeedScopeAsync(AsyncFakeRepository<Scope> scopes, string name = "Acme")
    {
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = name };
        await scopes.CreateAsync(scope);
        return scope;
    }

    private static async Task<Person> SeedScopeAdminAsync(
        AsyncFakeRepository<Person> persons, Scope? ownedScope = null, bool isDeleted = false)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Admin",
            Email = $"admin-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.ScopeAdmin,
            IsDeleted = isDeleted
        };

        if (ownedScope is not null)
        {
            person.ScopeOwnerships = [new ScopeOwner { ScopeId = ownedScope.Id }];
        }

        await persons.CreateAsync(person);
        return person;
    }

    private static async Task<Person> SeedScopeUserAsync(AsyncFakeRepository<Person> persons, Scope scope)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "User",
            Email = $"user-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.User,
            ScopeMembership = new ScopeUser { ScopeId = scope.Id }
        };
        await persons.CreateAsync(person);
        return person;
    }

    private static async Task<Person> SeedSystemAdminAsync(AsyncFakeRepository<Person> persons)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Root",
            Email = $"root-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.SystemAdmin
        };
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

    private static UpdateApplicationCommand Command(
        Guid scopeId, Guid id, Guid ownerId, int actingRole, Guid actingPersonId, string name = NewName) => new()
    {
        ScopeId = scopeId,
        Id = id,
        Name = name,
        OwnerId = ownerId,
        ActingRole = actingRole,
        ActingPersonId = actingPersonId
    };

    private static UpdateApplicationCommandHandler Handler(
        AsyncFakeRepository<Application> applications,
        AsyncFakeRepository<Person> persons,
        Mock<IValidator<UpdateApplicationCommand>>? validator = null) =>
        new((validator ?? ValidValidator()).Object, applications, persons, applications);

    [UnitFact]
    public async Task GivenSystemAdmin_WhenHandlingUpdateApplication_ThenApplicationIsUpdated()
    {
        // Given an application owned by a ScopeAdmin who owns its scope, renamed by a System Admin
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications, persons);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, owner.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);
        Assert.Equal(NewName, output.Data!.Name);
        Assert.Equal(owner.PublicId, output.Data.OwnerId);
        Assert.Contains(ApplicationMessages.ApplicationUpdatedSuccessfully, output.Messages);
        Assert.Equal(NewName, (await applications.GetAllAsync()).Data!.Single().Name);
    }

    [UnitFact]
    public async Task GivenOwningScopeAdmin_WhenHandlingUpdateApplication_ThenApplicationIsUpdated()
    {
        // Given the ScopeAdmin who owns the application renaming it (UC-18 step 3)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications, persons);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, owner.PublicId, (int)Roles.ScopeAdmin, owner.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Equal(NewName, output.Data!.Name);
    }

    [UnitFact]
    public async Task GivenUpdatedApplication_WhenHandlingUpdateApplication_ThenUpdatedAtIsStampedAndCreatedAtIsNot()
    {
        // Given an existing application (UC-18 step 5: no DB trigger maintains UpdatedAt)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var createdAt = application.CreatedAt;
        var before = DateTime.UtcNow;
        var handler = Handler(applications, persons);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, owner.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Data!.UpdatedAt >= before);
        Assert.Equal(createdAt, output.Data.CreatedAt);
        Assert.Equal(createdAt, (await applications.GetAllAsync()).Data!.Single().CreatedAt);
    }

    [UnitFact]
    public async Task GivenOutput_WhenHandlingUpdateApplication_ThenItCarriesPublicIdentifiers()
    {
        // Given internal ids that must never leave the data layer (SRD §4.0)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications, persons);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, owner.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.Equal(application.PublicId, output.Data!.Id);
        Assert.Equal(scope.PublicId, output.Data.ScopeId);
        Assert.Equal(owner.PublicId, output.Data.OwnerId);
    }

    [UnitFact]
    public async Task GivenOwningScopeAdminTransferringToACoOwner_WhenHandlingUpdateApplication_ThenOwnerChanges()
    {
        // Given the owning ScopeAdmin naming a co-owner of the scope as the new owner. UC-18 defines
        // no equivalent of UC-16's AF-16c, so giving away an application one owns is allowed
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var coOwner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications, persons);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, coOwner.PublicId, (int)Roles.ScopeAdmin, owner.PublicId));

        // Then — the response and the stored row both name the co-owner
        Assert.True(output.Success);
        Assert.Equal(coOwner.PublicId, output.Data!.OwnerId);
        Assert.Equal(coOwner.Id, (await applications.GetAllAsync()).Data!.Single().OwnerId);
    }

    [UnitFact]
    public async Task GivenUnchangedOwnerWhoIsNowLogicallyDeleted_WhenHandlingUpdateApplication_ThenApplicationIsUpdated()
    {
        // Given an application whose existing owner has since been logically deleted: main flow step 4
        // verifies only a *change* of owner, so a plain rename still goes through
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope, isDeleted: true);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications, persons);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, owner.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);
        Assert.Equal(NewName, output.Data!.Name);
        Assert.Equal(owner.PublicId, output.Data.OwnerId);
    }

    [UnitFact]
    public async Task GivenUnknownApplication_WhenHandlingUpdateApplication_ThenApplicationNotFoundIsReported()
    {
        // Given an application id nobody holds (AF-18a)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var handler = Handler(applications, persons);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, Guid.NewGuid(), owner.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.ApplicationNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenApplicationOfADifferentScope_WhenHandlingUpdateApplication_ThenApplicationNotFoundIsReported()
    {
        // Given an application addressed through a scope it does not belong to (AF-18a, Decision 3)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes, "Other");
        var owner = await SeedScopeAdminAsync(persons, ownedScope: otherScope);
        var application = await SeedApplicationAsync(applications, otherScope, owner);
        var handler = Handler(applications, persons);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, owner.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then — and the row keeps its original name
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.ApplicationNotFound, output.Errors);
        Assert.Equal("Billing Service", (await applications.GetAllAsync()).Data!.Single().Name);
    }

    [UnitFact]
    public async Task GivenUnknownScope_WhenHandlingUpdateApplication_ThenApplicationNotFoundIsReported()
    {
        // Given a scope id nobody holds (AF-18a, Decision 3)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications, persons);

        // When
        var output = await handler.HandleAsync(Command(
            Guid.NewGuid(), application.PublicId, owner.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.ApplicationNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedApplication_WhenHandlingUpdateApplication_ThenApplicationNotFoundIsReported()
    {
        // Given a logically deleted application: the precondition excludes it (AF-18a, Decision 8)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        await SeedApplicationAsync(applications, scope, owner, isDeleted: true);
        var application = (await applications.GetAllAsync()).Data!.Single();
        var handler = Handler(applications, persons);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, owner.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.ApplicationNotFound, output.Errors);
        Assert.Equal("Billing Service", (await applications.GetAllAsync()).Data!.Single().Name);
    }

    [UnitFact]
    public async Task GivenScopeAdminWhoOwnsTheScopeButNotTheApplication_WhenHandlingUpdateApplication_ThenNotAuthorizedIsReported()
    {
        // Given a co-owner of the scope acting on somebody else's application: owning the scope is not
        // grounds to modify it (AF-18c, Decision 2)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var coOwner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications, persons);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, coOwner.PublicId, (int)Roles.ScopeAdmin, coOwner.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.NotAuthorizedToUpdateApplication, output.Errors);
        Assert.Equal("Billing Service", (await applications.GetAllAsync()).Data!.Single().Name);
    }

    [UnitFact]
    public async Task GivenUnrelatedScopeAdmin_WhenHandlingUpdateApplication_ThenNotAuthorizedIsReported()
    {
        // Given a ScopeAdmin with nothing to do with this scope (AF-18c)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes, "Other");
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var stranger = await SeedScopeAdminAsync(persons, ownedScope: otherScope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications, persons);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, owner.PublicId, (int)Roles.ScopeAdmin, stranger.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.NotAuthorizedToUpdateApplication, output.Errors);
    }

    [UnitFact]
    public async Task GivenUnknownNewOwner_WhenHandlingUpdateApplication_ThenOwnerNotValidIsReported()
    {
        // Given an owner id nobody holds (AF-18b)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications, persons);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then — nothing moved
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.OwnerNotValidForScope, output.Errors);

        var stored = (await applications.GetAllAsync()).Data!.Single();
        Assert.Equal(owner.Id, stored.OwnerId);
        Assert.Equal("Billing Service", stored.Name);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedNewOwner_WhenHandlingUpdateApplication_ThenOwnerNotValidIsReported()
    {
        // Given a logically deleted ScopeAdmin as the proposed new owner (AF-18b)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var newOwner = await SeedScopeAdminAsync(persons, ownedScope: scope, isDeleted: true);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications, persons);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, newOwner.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.OwnerNotValidForScope, output.Errors);
    }

    [UnitFact]
    public async Task GivenNewOwnerWithUserRole_WhenHandlingUpdateApplication_ThenOwnerNotValidIsReported()
    {
        // Given a User of the scope as the proposed new owner: FR-AP-03 restricts ownership to a
        // ScopeAdmin who owns the scope, so belonging to it is not enough (AF-18b)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var newOwner = await SeedScopeUserAsync(persons, scope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications, persons);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, newOwner.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.OwnerNotValidForScope, output.Errors);
        Assert.Equal(owner.Id, (await applications.GetAllAsync()).Data!.Single().OwnerId);
    }

    [UnitFact]
    public async Task GivenNewOwnerWhoIsASystemAdmin_WhenHandlingUpdateApplication_ThenOwnerNotValidIsReported()
    {
        // Given a SystemAdmin person as the proposed new owner: they hold no SCOPE_OWNER row and do
        // not carry the ScopeAdmin role, so FR-AP-03 excludes them (AF-18b)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var newOwner = await SeedSystemAdminAsync(persons);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications, persons);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, newOwner.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.OwnerNotValidForScope, output.Errors);
    }

    [UnitFact]
    public async Task GivenNewOwnerScopeAdminOfADifferentScope_WhenHandlingUpdateApplication_ThenOwnerNotValidIsReported()
    {
        // Given a ScopeAdmin who owns another scope entirely (AF-18b)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes, "Other");
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var newOwner = await SeedScopeAdminAsync(persons, ownedScope: otherScope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var handler = Handler(applications, persons);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, newOwner.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.OwnerNotValidForScope, output.Errors);
    }

    [UnitFact]
    public async Task GivenInvalidInput_WhenHandlingUpdateApplication_ThenNothingIsChanged()
    {
        // Given a validator that rejects the command (UC-18 step 2)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var scope = await SeedScopeAsync(scopes);
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var application = await SeedApplicationAsync(applications, scope, owner);
        var validator = new Mock<IValidator<UpdateApplicationCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<UpdateApplicationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(
                [new ValidationFailure(nameof(UpdateApplicationCommand.Name), ApplicationMessages.NameRequired)]));
        var handler = Handler(applications, persons, validator);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, application.PublicId, owner.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid(),
            name: string.Empty));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.NameRequired, output.Errors);
        Assert.Equal("Billing Service", (await applications.GetAllAsync()).Data!.Single().Name);
    }
}
