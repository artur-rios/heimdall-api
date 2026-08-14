using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for HardDeletePersonCommandHandler (UC-10). Cover the main flow with and without
// dependents, on an already logically deleted person, and on a co-owned ScopeAdmin; AF-10a (not
// found), AF-10b (the NFR-12 last-owner guard, including the already-deleted target of Decision 4),
// and AF-10c (self-deletion).
//
// The [RoleRequirement] gate that keeps a ScopeAdmin and a User out of the endpoint is a Presentation
// concern and is asserted in PersonControllerHardDeleteTests (Testing Specification §6.4). So is the
// SCOPE_USER/SCOPE_OWNER cascade: the fakes are not foreign-key aware.
public class HardDeletePersonCommandHandlerTests
{
    // One fake per aggregate; each is passed as BOTH the reader and the writer argument.
    private sealed record Fakes(
        AsyncFakeRepository<Person> Persons,
        AsyncFakeRepository<Application> Applications,
        AsyncFakeRepository<PasswordResetToken> PasswordResetTokens,
        AsyncFakeRepository<EmailVerificationToken> EmailVerificationTokens)
    {
        public HardDeletePersonCommandHandler Handler() => new(
            Persons, Persons,
            Applications, Applications,
            PasswordResetTokens, PasswordResetTokens,
            EmailVerificationTokens, EmailVerificationTokens);
    }

    private static Fakes EmptyFakes() => new(
        new AsyncFakeRepository<Person>(),
        new AsyncFakeRepository<Application>(),
        new AsyncFakeRepository<PasswordResetToken>(),
        new AsyncFakeRepository<EmailVerificationToken>());

    private static Scope Scope(long id) => new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}" };

    private static async Task<Person> SeedUserAsync(Fakes fakes, Scope scope, bool isDeleted = false)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Member",
            Email = $"user-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.User,
            IsDeleted = isDeleted,
            ScopeId = scope.Id, ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope }
        };
        await fakes.Persons.CreateAsync(person);

        return person;
    }

    private static async Task<Person> SeedScopeAdminAsync(Fakes fakes, bool isDeleted = false, params Scope[] owned)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Admin",
            Email = $"admin-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.ScopeAdmin,
            IsDeleted = isDeleted,
            ScopeOwnerships = owned.Select(scope => new ScopeOwner { ScopeId = scope.Id, Scope = scope }).ToList()
        };
        await fakes.Persons.CreateAsync(person);

        return person;
    }

    private static async Task<Application> SeedApplicationAsync(Fakes fakes, Person owner, bool isDeleted = false)
    {
        var application = new Application
        {
            PublicId = Guid.NewGuid(),
            Name = $"app-{Guid.NewGuid():N}",
            ScopeId = 1,
            OwnerId = owner.Id,
            IsDeleted = isDeleted
        };
        await fakes.Applications.CreateAsync(application);

        return application;
    }

    private static async Task SeedTokensAsync(Fakes fakes, Person person)
    {
        await fakes.PasswordResetTokens.CreateAsync(new PasswordResetToken
        {
            PersonId = person.Id, TokenHash = SingleUseTokenHash.Of(Guid.NewGuid().ToString("N")),
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await fakes.EmailVerificationTokens.CreateAsync(new EmailVerificationToken
        {
            PersonId = person.Id, TokenHash = SingleUseTokenHash.Of(Guid.NewGuid().ToString("N")),
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
    }

    private static HardDeletePersonCommand CommandFor(Person target, Guid? actingPersonId = null) => new()
    {
        Id = target.PublicId,
        ActingRole = (int)Roles.SystemAdmin,
        ActingPersonId = actingPersonId ?? Guid.NewGuid()
    };

    [UnitFact]
    public async Task GivenPersonWithDependents_WhenHandlingHardDeletePerson_ThenPersonAndDependentsAreRemoved()
    {
        // Given a User owning one application and holding one token of each kind
        var fakes = EmptyFakes();
        var target = await SeedUserAsync(fakes, Scope(1));
        await SeedApplicationAsync(fakes, target);
        await SeedTokensAsync(fakes, target);
        var command = CommandFor(target);

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — output
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.Equal(target.PublicId, output.Data!.Id);
        Assert.Equal(1, output.Data.DeletedApplicationCount);
        Assert.Equal(2, output.Data.DeletedTokenCount);
        Assert.Contains(PersonMessages.PersonHardDeletedSuccessfully, output.Messages);

        // Then — every store is empty
        Assert.Empty((await fakes.Persons.GetAllAsync()).Data!);
        Assert.Empty((await fakes.Applications.GetAllAsync()).Data!);
        Assert.Empty((await fakes.PasswordResetTokens.GetAllAsync()).Data!);
        Assert.Empty((await fakes.EmailVerificationTokens.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenPersonWithNoDependents_WhenHandlingHardDeletePerson_ThenPersonIsRemovedWithZeroCounts()
    {
        // Given
        var fakes = EmptyFakes();
        var target = await SeedUserAsync(fakes, Scope(1));
        var command = CommandFor(target);

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal(0, output.Data!.DeletedApplicationCount);
        Assert.Equal(0, output.Data.DeletedTokenCount);
        Assert.Empty((await fakes.Persons.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedPerson_WhenHandlingHardDeletePerson_ThenPersonIsRemoved()
    {
        // Given a person already soft-deleted: hard deletion works in any deletion state
        var fakes = EmptyFakes();
        var target = await SeedUserAsync(fakes, Scope(1), isDeleted: true);
        await SeedApplicationAsync(fakes, target, isDeleted: true);
        var command = CommandFor(target);

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — the already-deleted application is still counted and still removed
        Assert.True(output.Success);
        Assert.Equal(1, output.Data!.DeletedApplicationCount);
        Assert.Empty((await fakes.Persons.GetAllAsync()).Data!);
        Assert.Empty((await fakes.Applications.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenScopeAdminWithCoOwnedScopes_WhenHandlingHardDeletePerson_ThenPersonIsRemoved()
    {
        // Given a ScopeAdmin whose only owned scope has another owner
        var fakes = EmptyFakes();
        var scope = Scope(1);
        var target = await SeedScopeAdminAsync(fakes, owned: scope);
        var coOwner = await SeedScopeAdminAsync(fakes, owned: scope);
        var command = CommandFor(target);

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — the target is gone, the co-owner remains
        Assert.True(output.Success);
        var remaining = (await fakes.Persons.GetAllAsync()).Data!;
        Assert.Single(remaining);
        Assert.Equal(coOwner.PublicId, remaining.First().PublicId);
    }

    [UnitFact]
    public async Task GivenAnotherPersonsDependents_WhenHandlingHardDeletePerson_ThenTheyAreLeftAlone()
    {
        // Given two Users, each owning an application and holding tokens
        var fakes = EmptyFakes();
        var scope = Scope(1);
        var target = await SeedUserAsync(fakes, scope);
        var bystander = await SeedUserAsync(fakes, scope);
        await SeedApplicationAsync(fakes, target);
        var bystanderApplication = await SeedApplicationAsync(fakes, bystander);
        await SeedTokensAsync(fakes, target);
        await SeedTokensAsync(fakes, bystander);
        var command = CommandFor(target);

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — only the target's rows are counted and removed
        Assert.True(output.Success);
        Assert.Equal(1, output.Data!.DeletedApplicationCount);
        Assert.Equal(2, output.Data.DeletedTokenCount);

        var remainingApplications = (await fakes.Applications.GetAllAsync()).Data!;
        Assert.Single(remainingApplications);
        Assert.Equal(bystanderApplication.PublicId, remainingApplications.First().PublicId);
        Assert.Single((await fakes.PasswordResetTokens.GetAllAsync()).Data!);
        Assert.Single((await fakes.EmailVerificationTokens.GetAllAsync()).Data!);
        Assert.Single((await fakes.Persons.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenPersonDoesNotExist_WhenHandlingHardDeletePerson_ThenReturnsPersonNotFoundError()
    {
        // Given — AF-10a
        var fakes = EmptyFakes();
        var command = new HardDeletePersonCommand
        {
            Id = Guid.NewGuid(), ActingRole = (int)Roles.SystemAdmin, ActingPersonId = Guid.NewGuid()
        };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenSoleOwnerScopeAdmin_WhenHandlingHardDeletePerson_ThenReturnsScopeWouldLoseLastOwnerError()
    {
        // Given — AF-10b: nobody else owns the scope
        var fakes = EmptyFakes();
        var target = await SeedScopeAdminAsync(fakes, owned: Scope(1));
        await SeedScopeAdminAsync(fakes, owned: Scope(2));
        await SeedApplicationAsync(fakes, target);
        var command = CommandFor(target);

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — refused, and nothing was removed
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeWouldLoseLastOwner, output.Errors);
        Assert.Equal(2, (await fakes.Persons.GetAllAsync()).Data!.Count());
        Assert.Single((await fakes.Applications.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedSoleOwner_WhenHandlingHardDeletePerson_ThenStillReturnsScopeWouldLoseLastOwnerError()
    {
        // Given a sole owner who is already soft-deleted: the guard applies anyway, unlike UC-09,
        // where the idempotent AF-09b wins over it
        var fakes = EmptyFakes();
        var target = await SeedScopeAdminAsync(fakes, isDeleted: true, owned: Scope(1));
        var command = CommandFor(target);

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeWouldLoseLastOwner, output.Errors);
        Assert.Single((await fakes.Persons.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenActorTargetingThemselves_WhenHandlingHardDeletePerson_ThenReturnsCannotDeleteSelfError()
    {
        // Given — AF-10c, even for a System Admin
        var fakes = EmptyFakes();
        var target = await SeedUserAsync(fakes, Scope(1));
        target.RoleId = (long)Roles.SystemAdmin;
        var command = CommandFor(target, actingPersonId: target.PublicId);

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.CannotDeleteSelf, output.Errors);
        Assert.Single((await fakes.Persons.GetAllAsync()).Data!);
    }
}
