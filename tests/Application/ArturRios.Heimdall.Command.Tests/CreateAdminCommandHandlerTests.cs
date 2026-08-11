using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for CreateAdminCommandHandler (UC-06 path b): main flow + AF-06a (duplicate admin
// email system-wide). AF-06c (non-System-Admin) and AF-06d (invalid input) are functional/validator
// concerns.
public class CreateAdminCommandHandlerTests
{
    private static Mock<IValidator<CreateAdminCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<CreateAdminCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<CreateAdminCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator;
    }

    private static CreateAdminCommand Command(int role) => new()
    {
        Name = "Admin",
        Email = $"admin-{Guid.NewGuid():N}@test.local",
        Password = "Str0ngPass!",
        Role = role
    };

    [UnitFact]
    public async Task GivenUniqueEmail_WhenHandlingCreateAdmin_ThenScopeAdminIsCreatedWithoutJoinRow()
    {
        // Given
        var persons = new AsyncFakeRepository<Person>();
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateAdminCommandHandler(ValidValidator().Object, persons, persons, email.Object);
        var command = Command((int)Roles.ScopeAdmin);

        // When
        var output = await handler.HandleAsync(command);

        // Then — output
        Assert.True(output.Success);
        Assert.Equal((int)Roles.ScopeAdmin, output.Data!.Role);
        Assert.Null(output.Data.ScopeId);
        Assert.Contains(PersonMessages.PersonCreatedSuccessfully, output.Messages);

        // Then — a person was stored with RoleId=ScopeAdmin, EmailVerified=false, no membership/ownership
        var stored = (await persons.GetAllAsync()).Data!.Single();
        Assert.Equal((long)Roles.ScopeAdmin, stored.RoleId);
        Assert.False(stored.EmailVerified);
        Assert.NotEmpty(stored.PasswordHash);
        Assert.NotEmpty(stored.Salt);
        Assert.Null(stored.ScopeMembership);
        Assert.Empty(stored.ScopeOwnerships);

        // Then — a verification email was issued
        email.Verify(e => e.IssueAndSendAsync(It.IsAny<Person>()), Times.Once);
    }

    [UnitFact]
    public async Task GivenSystemAdminRole_WhenHandlingCreateAdmin_ThenSystemAdminIsCreated()
    {
        var persons = new AsyncFakeRepository<Person>();
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateAdminCommandHandler(ValidValidator().Object, persons, persons, email.Object);

        var output = await handler.HandleAsync(Command((int)Roles.SystemAdmin));

        Assert.True(output.Success);
        Assert.Equal((int)Roles.SystemAdmin, output.Data!.Role);
        Assert.Equal((long)Roles.SystemAdmin, (await persons.GetAllAsync()).Data!.Single().RoleId);
    }

    [UnitFact]
    public async Task GivenExistingAdminEmail_WhenHandlingCreateAdmin_ThenReturnsEmailAlreadyExists()
    {
        // Given an existing ScopeAdmin with the same email (AF-06a)
        var persons = new AsyncFakeRepository<Person>();
        var command = Command((int)Roles.ScopeAdmin);
        await persons.CreateAsync(new Person
        {
            Email = command.Email, RoleId = (long)Roles.ScopeAdmin, IsDeleted = false
        });
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateAdminCommandHandler(ValidValidator().Object, persons, persons, email.Object);

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
        email.Verify(e => e.IssueAndSendAsync(It.IsAny<Person>()), Times.Never);
    }

    [UnitFact]
    public async Task GivenExistingAdminEmailDifferentCase_WhenHandlingCreateAdmin_ThenReturnsEmailAlreadyExists()
    {
        // Given an existing ScopeAdmin whose email differs from the request only by case (AF-06a is
        // case-insensitive)
        var persons = new AsyncFakeRepository<Person>();
        var command = Command((int)Roles.ScopeAdmin);
        await persons.CreateAsync(new Person
        {
            Email = command.Email.ToUpperInvariant(), RoleId = (long)Roles.ScopeAdmin, IsDeleted = false
        });
        var email = new Mock<IEmailVerificationService>();
        var handler = new CreateAdminCommandHandler(ValidValidator().Object, persons, persons, email.Object);

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
        email.Verify(e => e.IssueAndSendAsync(It.IsAny<Person>()), Times.Never);
    }
}
