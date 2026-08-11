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

namespace ArturRios.Heimdall.Command.Tests;

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

    private static async Task<(AsyncFakeRepository<Person>, Guid)> PersonsWithScopeAdminOwner()
    {
        var persons = new AsyncFakeRepository<Person>();
        var ownerId = Guid.NewGuid();
        await persons.CreateAsync(new Person
        {
            PublicId = ownerId, RoleId = (long)Roles.ScopeAdmin, IsDeleted = false
        });
        return (persons, ownerId);
    }

    [UnitFact]
    public async Task GivenUniqueNameAndValidOwner_WhenHandlingCreateScope_ThenScopeIsCreated()
    {
        // Given
        var (persons, ownerId) = await PersonsWithScopeAdminOwner();
        var scopes = new AsyncFakeRepository<Scope>();
        var handler = new CreateScopeCommandHandler(ValidValidator().Object, scopes, persons, scopes);
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
        var (persons, ownerId) = await PersonsWithScopeAdminOwner();
        var scopes = new AsyncFakeRepository<Scope>();
        await scopes.CreateAsync(new Scope { Name = "Acme" });
        var handler = new CreateScopeCommandHandler(ValidValidator().Object, scopes, persons, scopes);
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
        var persons = new AsyncFakeRepository<Person>();
        var scopes = new AsyncFakeRepository<Scope>();
        var handler = new CreateScopeCommandHandler(validator.Object, scopes, persons, scopes);
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
        // Given a person with the User role, not a ScopeAdmin (AF-01d)
        var persons = new AsyncFakeRepository<Person>();
        var ownerId = Guid.NewGuid();
        await persons.CreateAsync(new Person { PublicId = ownerId, RoleId = (long)Roles.User });
        var scopes = new AsyncFakeRepository<Scope>();
        var handler = new CreateScopeCommandHandler(ValidValidator().Object, scopes, persons, scopes);
        var command = new CreateScopeCommand { Name = "Acme", OwnerIds = [ownerId] };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.OwnerNotValidScopeAdmin, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedScopeAdminOwner_WhenHandlingCreateScope_ThenReturnsOwnerNotValidError()
    {
        // Given a logically deleted ScopeAdmin named as the initial owner (AF-01d)
        var persons = new AsyncFakeRepository<Person>();
        var ownerId = Guid.NewGuid();
        await persons.CreateAsync(new Person
        {
            PublicId = ownerId, RoleId = (long)Roles.ScopeAdmin, IsDeleted = true
        });
        var scopes = new AsyncFakeRepository<Scope>();
        var handler = new CreateScopeCommandHandler(ValidValidator().Object, scopes, persons, scopes);
        var command = new CreateScopeCommand { Name = "Acme", OwnerIds = [ownerId] };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.OwnerNotValidScopeAdmin, output.Errors);
    }

    [UnitFact]
    public async Task GivenNameExistsDifferentCase_WhenHandlingCreateScope_ThenReturnsNameAlreadyExistsError()
    {
        // Given a store already containing a scope named "Acme"; the request differs only by case
        // (name uniqueness is case-insensitive)
        var (persons, ownerId) = await PersonsWithScopeAdminOwner();
        var scopes = new AsyncFakeRepository<Scope>();
        await scopes.CreateAsync(new Scope { Name = "Acme" });
        var handler = new CreateScopeCommandHandler(ValidValidator().Object, scopes, persons, scopes);
        var command = new CreateScopeCommand { Name = "ACME", OwnerIds = [ownerId] };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.NameAlreadyExists, output.Errors);
    }
}
