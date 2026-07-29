using ArturRios.IdentityManager.Command.Handlers;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Services;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for CreateScopeOwnerCommandHandler (UC-06 path c): main flow + AF-06b (scope
// missing/deleted), AF-06e delegation (checker rejects the actor), AF-06a (duplicate admin email
// system-wide, case-insensitive). The AF-06e ownership rule itself is covered by
// ScopeOwnershipCheckerTests.
public class CreateScopeOwnerCommandHandlerTests
{
    private static Mock<IValidator<CreateScopeOwnerCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<CreateScopeOwnerCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<CreateScopeOwnerCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator;
    }

    private static IScopeOwnershipChecker OwnershipChecker(bool allowed = true)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(c => c.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<long>()))
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

    private static CreateScopeOwnerCommand Command(Guid scopeId, int actingRole, long actingPersonId) => new()
    {
        ScopeId = scopeId,
        Name = "Owner",
        Email = $"owner-{Guid.NewGuid():N}@test.local",
        Password = "Str0ngPass!",
        ActingRole = actingRole,
        ActingPersonId = actingPersonId
    };

    [UnitFact]
    public async Task GivenSystemAdminAndUniqueEmail_WhenHandlingCreateScopeOwner_ThenScopeAdminWithOwnershipIsCreated()
    {
        // Given a SystemAdmin actor (ownership allowed) and an empty scope
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateScopeOwnerCommandHandler(
            ValidValidator().Object, scopes, persons, persons, OwnershipChecker(), email.Object);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, (int)Roles.SystemAdmin, 1));

        // Then — output
        Assert.True(output.Success);
        Assert.Equal(scope.PublicId, output.Data!.ScopeId);
        Assert.Equal((int)Roles.ScopeAdmin, output.Data.Role);
        Assert.Contains(PersonMessages.PersonCreatedSuccessfully, output.Messages);

        // Then — a ScopeAdmin with a SCOPE_OWNER row for the scope
        var stored = (await persons.GetAllAsync()).Data!.Single();
        Assert.Equal((long)Roles.ScopeAdmin, stored.RoleId);
        Assert.Equal(scope.Id, Assert.Single(stored.ScopeOwnerships).ScopeId);
        email.Verify(e => e.IssueAndSendAsync(It.IsAny<Person>()), Times.Once);
    }

    [UnitFact]
    public async Task GivenActorNotAllowedForScope_WhenHandlingCreateScopeOwner_ThenReturnsNotScopeOwner()
    {
        // Given the ownership checker rejects the actor (AF-06e)
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateScopeOwnerCommandHandler(
            ValidValidator().Object, scopes, persons, persons, OwnershipChecker(allowed: false), email.Object);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, (int)Roles.ScopeAdmin, actingPersonId: 5));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotScopeOwner, output.Errors);
    }

    [UnitFact]
    public async Task GivenMissingScope_WhenHandlingCreateScopeOwner_ThenReturnsScopeNotFound()
    {
        var scopes = new AsyncFakeRepository<Scope>();
        var persons = new AsyncFakeRepository<Person>();
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateScopeOwnerCommandHandler(
            ValidValidator().Object, scopes, persons, persons, OwnershipChecker(), email.Object);

        var output = await handler.HandleAsync(Command(Guid.NewGuid(), (int)Roles.SystemAdmin, 1));

        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenExistingAdminEmail_WhenHandlingCreateScopeOwner_ThenReturnsEmailAlreadyExists()
    {
        // Given an existing ScopeAdmin with the same email system-wide (AF-06a)
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var command = Command(scope.PublicId, (int)Roles.SystemAdmin, 1);
        await persons.CreateAsync(new Person
        {
            Email = command.Email, RoleId = (long)Roles.ScopeAdmin, IsDeleted = false
        });
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateScopeOwnerCommandHandler(
            ValidValidator().Object, scopes, persons, persons, OwnershipChecker(), email.Object);

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
    }

    [UnitFact]
    public async Task GivenExistingAdminEmailDifferentCase_WhenHandlingCreateScopeOwner_ThenReturnsEmailAlreadyExists()
    {
        // Given an existing ScopeAdmin whose email differs from the request only by case (AF-06a is
        // case-insensitive)
        var (scopes, scope) = await ScopeStoreAsync();
        var persons = new AsyncFakeRepository<Person>();
        var command = Command(scope.PublicId, (int)Roles.SystemAdmin, 1);
        await persons.CreateAsync(new Person
        {
            Email = command.Email.ToUpperInvariant(), RoleId = (long)Roles.ScopeAdmin, IsDeleted = false
        });
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateScopeOwnerCommandHandler(
            ValidValidator().Object, scopes, persons, persons, OwnershipChecker(), email.Object);

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
    }
}
