using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for CreateApplicationCommandHandler (UC-16): main flow for a System Admin and for an
// owning Scope Admin naming themself, plus AF-16a (scope missing/deleted), AF-16b (owner is not a
// ScopeAdmin owning the scope), AF-16c (a Scope Admin naming a co-owner), AF-16d (invalid input),
// and AF-16e (a Scope Admin acting on a scope they do not own). A `User` never reaches the handler —
// [RoleRequirement] refuses them at the endpoint, covered in ApplicationControllerCreateTests. The
// ownership rule itself is covered by ScopeOwnershipCheckerTests.
public class CreateApplicationCommandHandlerTests
{
    private static Mock<IValidator<CreateApplicationCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<CreateApplicationCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<CreateApplicationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator;
    }

    private static IScopeOwnershipChecker OwnershipChecker(bool allowed = true)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(c => c.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);
        return checker.Object;
    }

    private static async Task<(AsyncFakeRepository<Scope> scopes, Scope scope)> ScopeStoreAsync(
        bool isDeleted = false)
    {
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = "Acme", IsDeleted = isDeleted };
        await scopes.CreateAsync(scope);
        return (scopes, scope);
    }

    private static async Task<Person> SeedScopeUserAsync(
        AsyncFakeRepository<Person> persons, Scope scope, bool isDeleted = false)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "User",
            Email = $"user-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.User,
            IsDeleted = isDeleted,
            ScopeMembership = new ScopeUser { ScopeId = scope.Id }
        };
        await persons.CreateAsync(person);
        return person;
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

    private static CreateApplicationCommand Command(
        Guid scopeId, Guid ownerId, int actingRole, Guid actingPersonId) => new()
    {
        ScopeId = scopeId,
        Name = "Billing Service",
        OwnerId = ownerId,
        ActingRole = actingRole,
        ActingPersonId = actingPersonId
    };

    private static CreateApplicationCommandHandler Handler(
        AsyncFakeRepository<Scope> scopes,
        AsyncFakeRepository<Person> persons,
        AsyncFakeRepository<Application> applications,
        IScopeOwnershipChecker? ownership = null,
        Mock<IValidator<CreateApplicationCommand>>? validator = null) =>
        new((validator ?? ValidValidator()).Object, scopes, persons, applications,
            ownership ?? OwnershipChecker());

    [UnitFact]
    public async Task GivenSystemAdminAndOwningScopeAdminOwner_WhenHandlingCreateApplication_ThenApplicationIsCreated()
    {
        // Given a SystemAdmin actor and an owner who is a ScopeAdmin owning the scope (FR-AP-03)
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var handler = Handler(scopes, persons, applications);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, owner.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);
        Assert.Equal("Billing Service", output.Data!.Name);
        Assert.Equal(scope.PublicId, output.Data.ScopeId);
        Assert.Equal(owner.PublicId, output.Data.OwnerId);
        Assert.Contains(ApplicationMessages.ApplicationCreatedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenOwningScopeAdminNamingThemself_WhenHandlingCreateApplication_ThenApplicationIsCreated()
    {
        // Given a ScopeAdmin who owns the scope and names themself as owner (matrix: "self as owner")
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var caller = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var handler = Handler(scopes, persons, applications, OwnershipChecker(allowed: true));

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, caller.PublicId, (int)Roles.ScopeAdmin, caller.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Equal(caller.PublicId, output.Data!.OwnerId);
    }

    [UnitFact]
    public async Task GivenCreatedApplication_WhenHandlingCreateApplication_ThenRowCarriesScopeAndOwnerInternalIds()
    {
        // Given
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var handler = Handler(scopes, persons, applications);

        // When
        await handler.HandleAsync(
            Command(scope.PublicId, owner.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then — the persisted row points at the internal ids and is not logically deleted
        var stored = (await applications.GetAllAsync()).Data!.Single();
        Assert.Equal(scope.Id, stored.ScopeId);
        Assert.Equal(owner.Id, stored.OwnerId);
        Assert.False(stored.IsDeleted);
        Assert.NotEqual(Guid.Empty, stored.PublicId);
    }

    [UnitFact]
    public async Task GivenMissingScope_WhenHandlingCreateApplication_ThenScopeNotFoundIsReported()
    {
        // Given an empty scope store (AF-16a)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var handler = Handler(scopes, persons, applications);

        // When
        var output = await handler.HandleAsync(
            Command(Guid.NewGuid(), Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.ScopeNotFound, output.Errors);
        Assert.Empty((await applications.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedScope_WhenHandlingCreateApplication_ThenScopeNotFoundIsReported()
    {
        // Given a logically deleted scope (AF-16a)
        var (scopes, scope) = await ScopeStoreAsync(isDeleted: true);
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var handler = Handler(scopes, persons, applications);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenScopeAdminWhoDoesNotOwnTheScope_WhenHandlingCreateApplication_ThenNotScopeOwnerIsReported()
    {
        // Given the ownership checker rejects the acting ScopeAdmin (AF-16e)
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var handler = Handler(scopes, persons, applications, OwnershipChecker(allowed: false));

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, owner.PublicId, (int)Roles.ScopeAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.NotScopeOwner, output.Errors);
        Assert.Empty((await applications.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenScopeAdminNamingACoOwner_WhenHandlingCreateApplication_ThenCannotSetAnotherOwnerIsReported()
    {
        // Given a ScopeAdmin who owns the scope naming a co-owner as the application's owner (AF-16c).
        // The co-owner would satisfy FR-AP-03, so the refusal is about who asked, not about the owner.
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var caller = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var coOwner = await SeedScopeAdminAsync(persons, ownedScope: scope);
        var handler = Handler(scopes, persons, applications, OwnershipChecker(allowed: true));

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, coOwner.PublicId, (int)Roles.ScopeAdmin, caller.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.CannotSetAnotherOwner, output.Errors);
        Assert.DoesNotContain(ApplicationMessages.OwnerNotValidForScope, output.Errors);
        Assert.Empty((await applications.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenUnknownOwner_WhenHandlingCreateApplication_ThenOwnerNotValidIsReported()
    {
        // Given an owner id nobody holds (AF-16b)
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var handler = Handler(scopes, persons, applications);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.OwnerNotValidForScope, output.Errors);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedOwner_WhenHandlingCreateApplication_ThenOwnerNotValidIsReported()
    {
        // Given a logically deleted ScopeAdmin owning the scope (AF-16b)
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var owner = await SeedScopeAdminAsync(persons, ownedScope: scope, isDeleted: true);
        var handler = Handler(scopes, persons, applications);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, owner.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.OwnerNotValidForScope, output.Errors);
    }

    [UnitFact]
    public async Task GivenOwnerWithUserRole_WhenHandlingCreateApplication_ThenOwnerNotValidIsReported()
    {
        // Given a User of the scope as the proposed owner: FR-AP-03 restricts ownership to a
        // ScopeAdmin who owns the scope, so a User is refused however well they belong to it (AF-16b)
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var owner = await SeedScopeUserAsync(persons, scope);
        var handler = Handler(scopes, persons, applications);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, owner.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.OwnerNotValidForScope, output.Errors);
        Assert.Empty((await applications.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenOwnerScopeAdminOfADifferentScope_WhenHandlingCreateApplication_ThenOwnerNotValidIsReported()
    {
        // Given a ScopeAdmin who owns another scope entirely (AF-16b)
        var (scopes, scope) = await ScopeStoreAsync();
        var otherScope = new Scope { PublicId = Guid.NewGuid(), Name = "Other" };
        await scopes.CreateAsync(otherScope);
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var stranger = await SeedScopeAdminAsync(persons, ownedScope: otherScope);
        var handler = Handler(scopes, persons, applications);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, stranger.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.OwnerNotValidForScope, output.Errors);
    }

    [UnitFact]
    public async Task GivenSystemAdminAsOwner_WhenHandlingCreateApplication_ThenOwnerNotValidIsReported()
    {
        // Given a SystemAdmin person as the proposed owner: they hold no SCOPE_OWNER row and do not
        // carry the ScopeAdmin role, so FR-AP-03 excludes them (AF-16b) without a rule of their own
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var systemAdmin = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Root",
            Email = $"root-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.SystemAdmin
        };
        await persons.CreateAsync(systemAdmin);
        var handler = Handler(scopes, persons, applications);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, systemAdmin.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.OwnerNotValidForScope, output.Errors);
    }

    [UnitFact]
    public async Task GivenInvalidInput_WhenHandlingCreateApplication_ThenNothingIsCreated()
    {
        // Given a validator that rejects the command (AF-16d)
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var applications = new AsyncFakeRepository<Application>();
        var validator = new Mock<IValidator<CreateApplicationCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<CreateApplicationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(
                [new ValidationFailure(nameof(CreateApplicationCommand.Name), ApplicationMessages.NameRequired)]));
        var handler = Handler(scopes, persons, applications, validator: validator);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.NameRequired, output.Errors);
        Assert.Empty((await applications.GetAllAsync()).Data!);
    }
}
