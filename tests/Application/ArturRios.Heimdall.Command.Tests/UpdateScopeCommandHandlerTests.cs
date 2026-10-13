using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

public class UpdateScopeCommandHandlerTests
{
    private static Mock<IValidator<UpdateScopeCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<UpdateScopeCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<UpdateScopeCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator;
    }

    private static async Task<AsyncFakeRepository<Scope, long>> RepositoryWith(params Scope[] scopes)
    {
        var repository = new AsyncFakeRepository<Scope, long>();

        foreach (var scope in scopes)
        {
            await repository.CreateAsync(scope);
        }

        return repository;
    }

    private static Scope ExistingScope(Guid publicId, string name, Guid ownerPublicId, bool isDeleted = false)
    {
        // Bogus builds the owner person; only the fields the behavior depends on are pinned.
        var owner = new Bogus.Faker<Person>()
            .RuleFor(p => p.PublicId, _ => ownerPublicId)
            .Generate();

        return new Scope
        {
            PublicId = publicId,
            Name = name,
            Description = "Original description",
            IsDeleted = isDeleted,
            Owners = [new ScopeOwner { Person = owner }]
        };
    }

    [UnitFact]
    public async Task GivenExistingScopeAndUniqueName_WhenHandlingUpdateScope_ThenScopeIsUpdated()
    {
        // Given
        var id = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var repository = await RepositoryWith(ExistingScope(id, "Old Name", ownerId));
        var handler = new UpdateScopeCommandHandler(ValidValidator().Object, repository, repository);
        var command = new UpdateScopeCommand { Id = id, Name = "New Name", Description = "New description" };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.Equal("New Name", output.Data!.Name);
        Assert.Equal("New description", output.Data.Description);
        Assert.Equal([ownerId], output.Data.OwnerIds);
        Assert.Contains(ScopeMessages.ScopeUpdatedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenNullDescription_WhenHandlingUpdateScope_ThenDescriptionIsCleared()
    {
        // Given
        var id = Guid.NewGuid();
        var repository = await RepositoryWith(ExistingScope(id, "Old Name", Guid.NewGuid()));
        var handler = new UpdateScopeCommandHandler(ValidValidator().Object, repository, repository);
        var command = new UpdateScopeCommand { Id = id, Name = "Old Name", Description = null };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Null(output.Data!.Description);
    }

    [UnitFact]
    public async Task GivenNameUnchanged_WhenHandlingUpdateScope_ThenNoFalseConflict()
    {
        // Given a scope keeping its own name
        var id = Guid.NewGuid();
        var repository = await RepositoryWith(ExistingScope(id, "Same Name", Guid.NewGuid()));
        var handler = new UpdateScopeCommandHandler(ValidValidator().Object, repository, repository);
        var command = new UpdateScopeCommand { Id = id, Name = "Same Name", Description = "Changed" };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal("Changed", output.Data!.Description);
    }

    [UnitFact]
    public async Task GivenMissingScope_WhenHandlingUpdateScope_ThenReturnsScopeNotFound()
    {
        // Given an empty store
        var repository = await RepositoryWith();
        var handler = new UpdateScopeCommandHandler(ValidValidator().Object, repository, repository);
        var command = new UpdateScopeCommand { Id = Guid.NewGuid(), Name = "New Name" };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedScope_WhenHandlingUpdateScope_ThenReturnsScopeNotFound()
    {
        // Given a scope that is logically deleted
        var id = Guid.NewGuid();
        var repository = await RepositoryWith(ExistingScope(id, "Old Name", Guid.NewGuid(), isDeleted: true));
        var handler = new UpdateScopeCommandHandler(ValidValidator().Object, repository, repository);
        var command = new UpdateScopeCommand { Id = id, Name = "New Name" };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenNameUsedByAnotherScope_WhenHandlingUpdateScope_ThenReturnsNameAlreadyExists()
    {
        // Given two scopes; the target will try to take the other's name
        var id = Guid.NewGuid();
        var repository = await RepositoryWith(
            ExistingScope(id, "Target", Guid.NewGuid()),
            ExistingScope(Guid.NewGuid(), "Taken", Guid.NewGuid()));
        var handler = new UpdateScopeCommandHandler(ValidValidator().Object, repository, repository);
        var command = new UpdateScopeCommand { Id = id, Name = "Taken" };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.NameAlreadyExists, output.Errors);
    }

    [UnitFact]
    public async Task GivenNameUsedByAnotherScopeDifferentCase_WhenHandlingUpdateScope_ThenReturnsNameAlreadyExists()
    {
        // Given two scopes; the target tries to take the other's name differing only by case
        // (name uniqueness is case-insensitive)
        var id = Guid.NewGuid();
        var repository = await RepositoryWith(
            ExistingScope(id, "Target", Guid.NewGuid()),
            ExistingScope(Guid.NewGuid(), "Taken", Guid.NewGuid()));
        var handler = new UpdateScopeCommandHandler(ValidValidator().Object, repository, repository);
        var command = new UpdateScopeCommand { Id = id, Name = "TAKEN" };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.NameAlreadyExists, output.Errors);
    }

    [UnitFact]
    public async Task GivenInvalidInput_WhenHandlingUpdateScope_ThenReturnsValidationError()
    {
        // Given a validator that reports a failure
        var validator = new Mock<IValidator<UpdateScopeCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<UpdateScopeCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([new ValidationFailure("Name", ScopeMessages.NameRequired)]));
        var repository = await RepositoryWith();
        var handler = new UpdateScopeCommandHandler(validator.Object, repository, repository);
        var command = new UpdateScopeCommand { Id = Guid.NewGuid(), Name = string.Empty };

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.NameRequired, output.Errors);
    }

    [UnitFact]
    public async Task GivenAScopeWithEmailLinks_WhenHandlingUpdateScope_ThenBothLinksSurvive()
    {
        // The links have their own endpoint precisely because this update replaces every field it
        // knows: a client that never heard of them must not erase them by renaming the scope.
        var id = Guid.NewGuid();
        var scope = ExistingScope(id, "Old Name", Guid.NewGuid());
        scope.EmailVerificationUrl = "https://app.test/verify-email";
        scope.PasswordResetUrl = "https://app.test/password-reset";
        var repository = await RepositoryWith(scope);
        var handler = new UpdateScopeCommandHandler(ValidValidator().Object, repository, repository);

        var output = await handler.HandleAsync(new UpdateScopeCommand { Id = id, Name = "New Name" });

        Assert.True(output.Success);
        var stored = repository.Query().Single();
        Assert.Equal("https://app.test/verify-email", stored.EmailVerificationUrl);
        Assert.Equal("https://app.test/password-reset", stored.PasswordResetUrl);
        Assert.Equal("https://app.test/verify-email", output.Data!.EmailVerificationUrl);
        Assert.Equal("https://app.test/password-reset", output.Data.PasswordResetUrl);
    }
}
