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

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for UpdatePersonCommandHandler (UC-08). Cover the main flow for each permitted actor,
// AF-08a (not found), AF-08b (email conflict), AF-08c (role change by a non-System-Admin), the
// unsupported transitions, and the NFR-12 last-owner guard.
//
// Note: AsyncFakeRepository is an in-memory list and models no EF cascade, so these tests assert
// that the scope navigation was cleared. That the scope_user / scope_owner row actually disappears
// is asserted by PersonControllerUpdateTests against PostgreSQL.
public class UpdatePersonCommandHandlerTests
{
    private static Scope Scope(long id) => new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}" };

    private static Person User(long id, Scope scope, string email = "user@test.local") => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"user-{id}",
        Email = email,
        RoleId = (long)Roles.User,
        EmailVerified = true,
        ScopeId = scope.Id, ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope }
    };

    private static Person ScopeAdmin(long id, string email = "admin@test.local", params Scope[] owned) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"admin-{id}",
        Email = email,
        RoleId = (long)Roles.ScopeAdmin,
        EmailVerified = true,
        ScopeOwnerships = owned.Select(scope => new ScopeOwner { ScopeId = scope.Id, Scope = scope }).ToList()
    };

    private static IValidator<UpdatePersonCommand> PassingValidator()
    {
        var validator = new Mock<IValidator<UpdatePersonCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<UpdatePersonCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        return validator.Object;
    }

    private static IScopeOwnershipChecker Ownership(bool allowed)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);

        return checker.Object;
    }

    private static async Task<AsyncFakeRepository<Person>> PersonsWith(params Person[] persons)
    {
        var repository = new AsyncFakeRepository<Person>();

        foreach (var person in persons)
        {
            await repository.CreateAsync(person);
        }

        return repository;
    }

    private static UpdatePersonCommandHandler HandlerFor(
        AsyncFakeRepository<Person> persons,
        bool ownershipAllowed = true,
        AsyncFakeRepository<GoogleUser>? googleUsers = null) =>
        new(
            PassingValidator(),
            persons,
            googleUsers ?? new AsyncFakeRepository<GoogleUser>(),
            persons,
            Ownership(ownershipAllowed));

    private static UpdatePersonCommand CommandFor(Person target, int actingRole, Guid actingPersonId) => new()
    {
        Id = target.PublicId,
        Name = target.Name,
        Email = target.Email,
        ActingRole = actingRole,
        ActingPersonId = actingPersonId
    };

    [UnitFact]
    public async Task GivenSystemAdminActor_WhenUpdatingNameAndEmail_ThenPersonIsUpdated()
    {
        // Given
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());
        command.Name = "Renamed";
        command.Email = "renamed@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal("Renamed", output.Data!.Name);
        Assert.Equal("renamed@test.local", output.Data.Email);
        Assert.False(output.Data.EmailVerified);
        Assert.Contains(PersonMessages.PersonUpdatedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenUserUpdatingSelf_WhenUpdatingName_ThenPersonIsUpdated()
    {
        // Given
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        command.Name = "Renamed";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal("Renamed", output.Data!.Name);
    }

    [UnitFact]
    public async Task GivenUnchangedEmail_WhenUpdating_ThenEmailVerifiedIsPreserved()
    {
        // Given a verified person whose email is resubmitted unchanged
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());
        command.Name = "Renamed";

        // When
        var output = await handler.HandleAsync(command);

        // Then — no false conflict, and the verification flag survives
        Assert.True(output.Success);
        Assert.True(output.Data!.EmailVerified);
    }

    [UnitFact]
    public async Task GivenOwningScopeAdmin_WhenUpdatingScopeUser_ThenPersonIsUpdated()
    {
        // Given a ScopeAdmin who owns the target User's scope
        var scope = Scope(1);
        var target = User(10, scope);
        var actor = ScopeAdmin(11, "owner@test.local", scope);
        var persons = await PersonsWith(target, actor);
        var handler = HandlerFor(persons, ownershipAllowed: true);
        var command = CommandFor(target, (int)Roles.ScopeAdmin, actor.PublicId);
        command.Name = "Renamed";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal("Renamed", output.Data!.Name);
    }

    [UnitFact]
    public async Task GivenNonOwningScopeAdmin_WhenUpdatingScopeUser_ThenReturnsNotAuthorized()
    {
        // Given a ScopeAdmin the ownership checker rejects
        var scope = Scope(1);
        var target = User(10, scope);
        var actor = ScopeAdmin(11, "outsider@test.local");
        var persons = await PersonsWith(target, actor);
        var handler = HandlerFor(persons, ownershipAllowed: false);

        // When
        var output = await handler.HandleAsync(CommandFor(target, (int)Roles.ScopeAdmin, actor.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotAuthorizedToUpdatePerson, output.Errors);
    }

    [UnitFact]
    public async Task GivenUserUpdatingAnotherPerson_WhenUpdating_ThenReturnsNotAuthorized()
    {
        // Given two Users of the same scope
        var scope = Scope(1);
        var target = User(10, scope);
        var actor = User(11, scope, "other@test.local");
        var persons = await PersonsWith(target, actor);
        var handler = HandlerFor(persons);

        // When
        var output = await handler.HandleAsync(CommandFor(target, (int)Roles.User, actor.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotAuthorizedToUpdatePerson, output.Errors);
    }

    [UnitFact]
    public async Task GivenUnknownPersonId_WhenUpdating_ThenReturnsPersonNotFound()
    {
        // Given an empty store (AF-08a)
        var persons = await PersonsWith();
        var handler = HandlerFor(persons);

        // When
        var output = await handler.HandleAsync(new UpdatePersonCommand
        {
            Id = Guid.NewGuid(), Name = "Ana", Email = "ana@test.local",
            ActingRole = (int)Roles.SystemAdmin, ActingPersonId = Guid.NewGuid()
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedPerson_WhenUpdating_ThenReturnsPersonNotFound()
    {
        // Given a logically deleted person (AF-08a)
        var scope = Scope(1);
        var target = User(10, scope);
        target.IsDeleted = true;
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);

        // When
        var output = await handler.HandleAsync(CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenEmailTakenByAnotherUserInScope_WhenUpdating_ThenReturnsEmailAlreadyExists()
    {
        // Given two Users in one scope (AF-08b, FR-PE-09 within scope)
        var scope = Scope(1);
        var target = User(10, scope);
        var other = User(11, scope, "taken@test.local");
        var persons = await PersonsWith(target, other);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.Email = "taken@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
    }

    [UnitFact]
    public async Task GivenEmailTakenByAnotherAdmin_WhenUpdatingAdmin_ThenReturnsEmailAlreadyExists()
    {
        // Given two admins (AF-08b, FR-PE-09 system-wide)
        var scope = Scope(1);
        var target = ScopeAdmin(10, "admin@test.local", scope);
        var other = ScopeAdmin(11, "taken@test.local", scope);
        var persons = await PersonsWith(target, other);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.Email = "taken@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
    }

    [UnitFact]
    public async Task GivenRoleChangeOntoAnAddressAnAdminHolds_WhenUpdating_ThenReturnsEmailAlreadyExists()
    {
        // Given a User and an administrator who share an address. That is legal while they are in
        // different namespaces — a User's address is unique within their scope, an admin's is unique
        // system-wide — but promoting the User to System Admin moves them into the admin namespace,
        // where the address is already taken.
        //
        // The uniqueness check used to run only when the *email* changed, so this promotion, which
        // does not touch the address, slipped past it. UC-11's admin lookup then resolves one of the
        // two rows and stops, leaving the other person unable to log in at all.
        var scope = Scope(1);
        var target = User(10, scope, "shared@test.local");
        var existingAdmin = ScopeAdmin(11, "shared@test.local");
        var persons = await PersonsWith(target, existingAdmin);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.RoleId = (int)Roles.SystemAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);

        // Then — nothing was written: the target keeps their role and their scope membership
        var stored = persons.Query().ToList().Single(person => person.Id == target.Id);
        Assert.Equal((long)Roles.User, stored.RoleId);
        Assert.NotNull(stored.ScopeMembership);
    }

    [UnitFact]
    public async Task GivenRoleChangeOntoAFreeAddress_WhenUpdating_ThenThePromotionSucceeds()
    {
        // Given the same promotion where nobody else holds the address. The re-check must not turn
        // an ordinary promotion into a conflict — the person being updated is excluded from it.
        var scope = Scope(1);
        var target = User(10, scope, "solo@test.local");
        var persons = await PersonsWith(target, ScopeAdmin(11, "someone-else@test.local"));
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.RoleId = (int)Roles.SystemAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);

        var stored = persons.Query().ToList().Single(person => person.Id == target.Id);
        Assert.Equal((long)Roles.SystemAdmin, stored.RoleId);
        Assert.Null(stored.ScopeMembership);

        // Then — the address was not touched, so verification survives (only an email change clears it)
        Assert.True(stored.EmailVerified);
    }

    [UnitFact]
    public async Task GivenEmailHeldByAGoogleUserOfTheScope_WhenUpdatingAUser_ThenReturnsEmailAlreadyExists()
    {
        // Given a scope whose address space it shares with its Google Users (FR-GO-07): moving a
        // User onto an address a Google User holds is the same conflict as moving them onto another
        // User's.
        var scope = Scope(1);
        var target = User(10, scope, "user@test.local");
        var persons = await PersonsWith(target);
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        await googleUsers.CreateAsync(new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = "google-sub",
            Email = "TAKEN@test.local",
            ScopeId = scope.Id
        });

        var handler = HandlerFor(persons, googleUsers: googleUsers);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.Email = "taken@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
        Assert.Equal("user@test.local", persons.Query().ToList().Single(x => x.Id == target.Id).Email);
    }

    [UnitFact]
    public async Task GivenNonSystemAdminActor_WhenChangingRole_ThenReturnsRoleChangeRequiresSystemAdmin()
    {
        // Given an owning ScopeAdmin attempting a role change (AF-08c)
        var scope = Scope(1);
        var target = User(10, scope);
        var actor = ScopeAdmin(11, "owner@test.local", scope);
        var persons = await PersonsWith(target, actor);
        var handler = HandlerFor(persons, ownershipAllowed: true);
        var command = CommandFor(target, (int)Roles.ScopeAdmin, actor.PublicId);
        command.RoleId = (int)Roles.SystemAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.RoleChangeRequiresSystemAdmin, output.Errors);
    }

    [UnitFact]
    public async Task GivenSystemAdminPromotingUserToScopeAdmin_WhenUpdating_ThenReturnsUnsupportedTransition()
    {
        // Given a User being pushed to ScopeAdmin, which would need a target scope
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.RoleId = (int)Roles.ScopeAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.UnsupportedRoleTransition, output.Errors);
    }

    [UnitFact]
    public async Task GivenSystemAdminPromotingUserToSystemAdmin_WhenUpdating_ThenScopeMembershipIsCleared()
    {
        // Given a User promoted to SystemAdmin, who must end up with no scope (FR-PE-10)
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.RoleId = (int)Roles.SystemAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal((int)Roles.SystemAdmin, output.Data!.Role);
        Assert.Null(output.Data.ScopeId);
        Assert.Null(target.ScopeMembership);
    }

    [UnitFact]
    public async Task GivenScopeWithAnotherOwner_WhenPromotingOwnerToSystemAdmin_ThenOwnershipsAreCleared()
    {
        // Given a scope with two owners, so losing one leaves it owned (NFR-12 satisfied)
        var scope = Scope(1);
        var target = ScopeAdmin(10, "first@test.local", scope);
        var coOwner = ScopeAdmin(11, "second@test.local", scope);
        var persons = await PersonsWith(target, coOwner);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.RoleId = (int)Roles.SystemAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal((int)Roles.SystemAdmin, output.Data!.Role);
        Assert.Empty(output.Data.OwnedScopeIds);
        Assert.Empty(target.ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenSoleOwner_WhenPromotingToSystemAdmin_ThenReturnsScopeWouldLoseLastOwner()
    {
        // Given a scope whose only owner is the target (NFR-12)
        var scope = Scope(1);
        var target = ScopeAdmin(10, "only@test.local", scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.RoleId = (int)Roles.SystemAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeWouldLoseLastOwner, output.Errors);
    }

    [UnitFact]
    public async Task GivenOnlyCoOwnerIsDeleted_WhenPromotingOwnerToSystemAdmin_ThenReturnsScopeWouldLoseLastOwner()
    {
        // Given a scope owned by the target and one logically deleted co-owner. A soft-deleted owner
        // can no longer authenticate, so the scope would be left effectively ownerless — the same
        // reading UC-09 AF-09e and UC-10 AF-10b apply (NFR-12).
        var scope = Scope(1);
        var target = ScopeAdmin(10, "first@test.local", scope);
        var deletedCoOwner = ScopeAdmin(11, "second@test.local", scope);
        deletedCoOwner.IsDeleted = true;
        var persons = await PersonsWith(target, deletedCoOwner);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.RoleId = (int)Roles.SystemAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeWouldLoseLastOwner, output.Errors);
        Assert.Equal((long)Roles.ScopeAdmin, target.RoleId);
        Assert.Single(target.ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenNullRoleId_WhenUpdating_ThenRoleIsUnchanged()
    {
        // Given a User whose command carries no role
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.Name = "Renamed";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal((int)Roles.User, output.Data!.Role);
        Assert.NotNull(target.ScopeMembership);
    }
}
