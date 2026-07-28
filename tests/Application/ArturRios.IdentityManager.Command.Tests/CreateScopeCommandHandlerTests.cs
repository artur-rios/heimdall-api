using ArturRios.IdentityManager.Command.Handlers;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for CreateScopeCommandHandler (UC-01).
// Cover the main flow plus alternative flows AF-01a (name exists), AF-01b (invalid input / no owner),
// and AF-01d (owner is not a valid ScopeAdmin). AF-01c (not System Admin) is a functional concern.
public class CreateScopeCommandHandlerTests
{
    private static Mock<IValidator<CreateScopeCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<CreateScopeCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<CreateScopeCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator;
    }

    private static async Task<AsyncFakeRepository<Role>> RolesWithScopeAdmin()
    {
        var roles = new AsyncFakeRepository<Role>();
        await roles.CreateAsync(new Role { Name = nameof(Roles.ScopeAdmin) });
        return roles;
    }

    private static async Task<(AsyncFakeRepository<Person>, Guid)> PersonsWithScopeAdminOwner(long scopeAdminRoleId)
    {
        var persons = new AsyncFakeRepository<Person>();
        var ownerId = Guid.NewGuid();
        await persons.CreateAsync(new Person { PublicId = ownerId, RoleId = scopeAdminRoleId, IsDeleted = false });
        return (persons, ownerId);
    }

    [UnitFact]
    public async Task GivenUniqueNameAndValidOwner_WhenHandlingCreateScope_ThenScopeIsCreated()
    {
        // Given
        var roles = await RolesWithScopeAdmin();
        var scopeAdminRoleId = (await roles.GetAllAsync()).Data!.Single().Id;
        var (persons, ownerId) = await PersonsWithScopeAdminOwner(scopeAdminRoleId);
        var scopes = new AsyncFakeRepository<Scope>();
        var handler = new CreateScopeCommandHandler(ValidValidator().Object, scopes, persons, roles, scopes);
        var command = new CreateScopeCommand { Name = "Acme", Description = "Acme scope", OwnerIds = [ownerId] };

        // When
        var output = await handler.HandleAsync(command);

        // Then — output
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.Equal("Acme", output.Data!.Name);
        Assert.Equal([ownerId], output.Data.OwnerIds);
        Assert.Contains(ScopeMessages.ScopeCreatedSuccessfully, output.Messages);

        // Then — a scope with one SCOPE_OWNER row was stored
        var stored = (await scopes.GetAllAsync()).Data!.Single();
        Assert.Equal("Acme", stored.Name);
        Assert.Single(stored.Owners);
    }

    [UnitFact]
    public async Task GivenNameAlreadyExists_WhenHandlingCreateScope_ThenReturnsNameAlreadyExistsError()
    {
        // Given a store that already contains a scope named "Acme"
        var roles = await RolesWithScopeAdmin();
        var scopeAdminRoleId = (await roles.GetAllAsync()).Data!.Single().Id;
        var (persons, ownerId) = await PersonsWithScopeAdminOwner(scopeAdminRoleId);
        var scopes = new AsyncFakeRepository<Scope>();
        await scopes.CreateAsync(new Scope { Name = "Acme" });
        var handler = new CreateScopeCommandHandler(ValidValidator().Object, scopes, persons, roles, scopes);
        var command = new CreateScopeCommand { Name = "Acme", OwnerIds = [ownerId] };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.NameAlreadyExists, output.Errors);
    }

    [UnitFact]
    public async Task GivenInvalidInput_WhenHandlingCreateScope_ThenReturnsValidationError()
    {
        // Given a validator that reports a failure (AF-01b)
        var validator = new Mock<IValidator<CreateScopeCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<CreateScopeCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([new ValidationFailure("OwnerIds", ScopeMessages.AtLeastOneOwnerRequired)]));
        var roles = await RolesWithScopeAdmin();
        var persons = new AsyncFakeRepository<Person>();
        var scopes = new AsyncFakeRepository<Scope>();
        var handler = new CreateScopeCommandHandler(validator.Object, scopes, persons, roles, scopes);
        var command = new CreateScopeCommand { Name = "Acme", OwnerIds = [] };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.AtLeastOneOwnerRequired, output.Errors);
    }

    [UnitFact]
    public async Task GivenOwnerIsNotScopeAdmin_WhenHandlingCreateScope_ThenReturnsOwnerNotValidError()
    {
        // Given a ScopeAdmin role but no matching owner person (AF-01d)
        var roles = await RolesWithScopeAdmin();
        var persons = new AsyncFakeRepository<Person>();
        var scopes = new AsyncFakeRepository<Scope>();
        var handler = new CreateScopeCommandHandler(ValidValidator().Object, scopes, persons, roles, scopes);
        var command = new CreateScopeCommand { Name = "Acme", OwnerIds = [Guid.NewGuid()] };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.OwnerNotValidScopeAdmin, output.Errors);
    }

    [UnitFact]
    public async Task GivenScopeAdminRoleNotConfigured_WhenHandlingCreateScope_ThenReturnsConfigurationError()
    {
        // Given no roles configured
        var roles = new AsyncFakeRepository<Role>();
        var persons = new AsyncFakeRepository<Person>();
        var scopes = new AsyncFakeRepository<Scope>();
        var handler = new CreateScopeCommandHandler(ValidValidator().Object, scopes, persons, roles, scopes);
        var command = new CreateScopeCommand { Name = "Acme", OwnerIds = [Guid.NewGuid()] };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeAdminRoleNotConfigured, output.Errors);
    }
}
