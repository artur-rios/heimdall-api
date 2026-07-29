using ArturRios.IdentityManager.Command.Handlers;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Services;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for CreateUserCommandHandler (UC-06 path a): main flow + AF-06b (scope missing/deleted),
// AF-06e (actor not owner / owner / SystemAdmin bypass), AF-06a (duplicate email in scope).
public class CreateUserCommandHandlerTests
{
    private static Mock<IValidator<CreateUserCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<CreateUserCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<CreateUserCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator;
    }

    private static async Task<(AsyncFakeRepository<Scope> scopes, Scope scope)> ScopeStoreAsync()
    {
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = "Acme", IsDeleted = false };
        await scopes.CreateAsync(scope);
        return (scopes, scope);
    }

    private static CreateUserCommand Command(Guid scopeId, int actingRole, long actingPersonId) => new()
    {
        ScopeId = scopeId,
        Name = "User",
        Email = $"user-{Guid.NewGuid():N}@test.local",
        Password = "Str0ngPass!",
        ActingRole = actingRole,
        ActingPersonId = actingPersonId
    };

    [UnitFact]
    public async Task GivenSystemAdminAndUniqueEmail_WhenHandlingCreateUser_ThenUserWithMembershipIsCreated()
    {
        // Given a SystemAdmin actor (bypasses ownership) and an empty scope
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateUserCommandHandler(ValidValidator().Object, scopes, persons, persons, email.Object);
        var command = Command(scope.PublicId, (int)Roles.SystemAdmin, actingPersonId: 1);

        // When
        var output = await handler.HandleAsync(command);

        // Then — output
        Assert.True(output.Success);
        Assert.Equal(scope.PublicId, output.Data!.ScopeId);
        Assert.Equal((int)Roles.User, output.Data.Role);
        Assert.Contains(PersonMessages.PersonCreatedSuccessfully, output.Messages);

        // Then — a User with a SCOPE_USER row pointing at the scope's internal Id
        var stored = (await persons.GetAllAsync()).Data!.Single();
        Assert.Equal((long)Roles.User, stored.RoleId);
        Assert.NotNull(stored.ScopeMembership);
        Assert.Equal(scope.Id, stored.ScopeMembership!.ScopeId);
        email.Verify(e => e.IssueAndSendAsync(It.IsAny<Person>()), Times.Once);
    }

    [UnitFact]
    public async Task GivenOwnerScopeAdmin_WhenHandlingCreateUser_ThenUserIsCreated()
    {
        // Given a ScopeAdmin actor who owns the scope
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var actor = new Person
        {
            RoleId = (long)Roles.ScopeAdmin,
            ScopeOwnerships = [new ScopeOwner { ScopeId = scope.Id }]
        };
        await persons.CreateAsync(actor);
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateUserCommandHandler(ValidValidator().Object, scopes, persons, persons, email.Object);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, (int)Roles.ScopeAdmin, actor.Id));

        // Then
        Assert.True(output.Success);
        Assert.Equal((int)Roles.User, output.Data!.Role);
    }

    [UnitFact]
    public async Task GivenScopeAdminNotOwner_WhenHandlingCreateUser_ThenReturnsNotScopeOwner()
    {
        // Given a ScopeAdmin actor with no ownership of the scope (AF-06e)
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var actor = new Person { RoleId = (long)Roles.ScopeAdmin };
        await persons.CreateAsync(actor);
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateUserCommandHandler(ValidValidator().Object, scopes, persons, persons, email.Object);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, (int)Roles.ScopeAdmin, actor.Id));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotScopeOwner, output.Errors);
        email.Verify(e => e.IssueAndSendAsync(It.IsAny<Person>()), Times.Never);
    }

    [UnitFact]
    public async Task GivenMissingScope_WhenHandlingCreateUser_ThenReturnsScopeNotFound()
    {
        // Given an empty scope store (AF-06b)
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateUserCommandHandler(ValidValidator().Object, scopes, persons, persons, email.Object);

        // When
        var output = await handler.HandleAsync(Command(Guid.NewGuid(), (int)Roles.SystemAdmin, 1));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDuplicateEmailInScope_WhenHandlingCreateUser_ThenReturnsEmailAlreadyExists()
    {
        // Given a scope that already has a User with the target email (AF-06a)
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var command = Command(scope.PublicId, (int)Roles.SystemAdmin, 1);
        await persons.CreateAsync(new Person
        {
            Email = command.Email,
            RoleId = (long)Roles.User,
            IsDeleted = false,
            ScopeMembership = new ScopeUser { ScopeId = scope.Id }
        });
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateUserCommandHandler(ValidValidator().Object, scopes, persons, persons, email.Object);

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
    }

    [UnitFact]
    public async Task GivenDuplicateEmailInScopeDifferentCase_WhenHandlingCreateUser_ThenReturnsEmailAlreadyExists()
    {
        // Given a scope User whose email differs from the request only by case (AF-06a is
        // case-insensitive)
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var command = Command(scope.PublicId, (int)Roles.SystemAdmin, 1);
        await persons.CreateAsync(new Person
        {
            Email = command.Email.ToUpperInvariant(),
            RoleId = (long)Roles.User,
            IsDeleted = false,
            ScopeMembership = new ScopeUser { ScopeId = scope.Id }
        });
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateUserCommandHandler(ValidValidator().Object, scopes, persons, persons, email.Object);

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
    }
}
