using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
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

// Unit tests for CreateUserCommandHandler (UC-06 path a): main flow + AF-06b (scope missing/deleted),
// AF-06e delegation (checker allows / rejects the actor), AF-06a (duplicate email in scope,
// case-insensitive). The AF-06e ownership rule itself is covered by ScopeOwnershipCheckerTests.
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

    private static IScopeOwnershipChecker OwnershipChecker(bool allowed = true)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(c => c.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);
        return checker.Object;
    }

    private static async Task<(AsyncFakeRepository<Scope> scopes, Scope scope)> ScopeStoreAsync()
    {
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = "Acme", IsDeleted = false };
        await scopes.CreateAsync(scope);
        return (scopes, scope);
    }

    private static CreateUserCommand Command(Guid scopeId, int actingRole, Guid actingPersonId) => new()
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
        // Given a SystemAdmin actor (ownership allowed) and an empty scope
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateUserCommandHandler(
            ValidValidator().Object, scopes, persons, persons, OwnershipChecker(), email.Object);
        var command = Command(scope.PublicId, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());

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
    public async Task GivenActorAllowedForScope_WhenHandlingCreateUser_ThenUserIsCreated()
    {
        // Given the ownership checker allows the actor (e.g. a ScopeAdmin who owns the scope)
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateUserCommandHandler(
            ValidValidator().Object, scopes, persons, persons, OwnershipChecker(allowed: true), email.Object);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, (int)Roles.ScopeAdmin, actingPersonId: Guid.NewGuid()));

        // Then
        Assert.True(output.Success);
        Assert.Equal((int)Roles.User, output.Data!.Role);
    }

    [UnitFact]
    public async Task GivenActorNotAllowedForScope_WhenHandlingCreateUser_ThenReturnsNotScopeOwner()
    {
        // Given the ownership checker rejects the actor (AF-06e)
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateUserCommandHandler(
            ValidValidator().Object, scopes, persons, persons, OwnershipChecker(allowed: false), email.Object);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, (int)Roles.ScopeAdmin, actingPersonId: Guid.NewGuid()));

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
        var handler = new CreateUserCommandHandler(
            ValidValidator().Object, scopes, persons, persons, OwnershipChecker(), email.Object);

        // When
        var output = await handler.HandleAsync(Command(Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid()));

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
        var command = Command(scope.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid());
        await persons.CreateAsync(new Person
        {
            Email = command.Email,
            RoleId = (long)Roles.User,
            IsDeleted = false,
            ScopeMembership = new ScopeUser { ScopeId = scope.Id }
        });
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateUserCommandHandler(
            ValidValidator().Object, scopes, persons, persons, OwnershipChecker(), email.Object);

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
        var command = Command(scope.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid());
        await persons.CreateAsync(new Person
        {
            Email = command.Email.ToUpperInvariant(),
            RoleId = (long)Roles.User,
            IsDeleted = false,
            ScopeMembership = new ScopeUser { ScopeId = scope.Id }
        });
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateUserCommandHandler(
            ValidValidator().Object, scopes, persons, persons, OwnershipChecker(), email.Object);

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
    }
}
