using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Input.Validation;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for UpdatePersonCommandValidator (UC-08 shape validation). Business rules that need
// data access — existence, authorization, email uniqueness, ownership — are the handler's job.
public class UpdatePersonCommandValidatorTests
{
    private static UpdatePersonCommand Valid() => new()
    {
        Id = Guid.NewGuid(), Name = "Ana", Email = "ana@test.local"
    };

    [UnitFact]
    public async Task GivenValidCommandWithoutRole_WhenValidating_ThenIsValid()
    {
        // Given
        var validator = new UpdatePersonCommandValidator();

        // When
        var result = await validator.ValidateAsync(Valid());

        // Then
        Assert.True(result.IsValid);
    }

    [UnitTheory]
    [InlineData((int)Roles.SystemAdmin)]
    [InlineData((int)Roles.ScopeAdmin)]
    [InlineData((int)Roles.User)]
    public async Task GivenDefinedRole_WhenValidating_ThenIsValid(int role)
    {
        // Given
        var validator = new UpdatePersonCommandValidator();
        var command = Valid();
        command.RoleId = role;

        // When
        var result = await validator.ValidateAsync(command);

        // Then
        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenUndefinedRole_WhenValidating_ThenReturnsUnknownRole()
    {
        // Given
        var validator = new UpdatePersonCommandValidator();
        var command = Valid();
        command.RoleId = 99;

        // When
        var result = await validator.ValidateAsync(command);

        // Then
        Assert.False(result.IsValid);
        Assert.Contains(PersonMessages.UnknownRole, result.Errors.Select(x => x.ErrorMessage));
    }

    [UnitFact]
    public async Task GivenEmptyName_WhenValidating_ThenReturnsNameRequired()
    {
        // Given
        var validator = new UpdatePersonCommandValidator();
        var command = Valid();
        command.Name = string.Empty;

        // When
        var result = await validator.ValidateAsync(command);

        // Then
        Assert.False(result.IsValid);
        Assert.Contains(PersonMessages.NameRequired, result.Errors.Select(x => x.ErrorMessage));
    }

    [UnitFact]
    public async Task GivenNameOver200Characters_WhenValidating_ThenReturnsNameTooLong()
    {
        // Given
        var validator = new UpdatePersonCommandValidator();
        var command = Valid();
        command.Name = new string('a', 201);

        // When
        var result = await validator.ValidateAsync(command);

        // Then
        Assert.False(result.IsValid);
        Assert.Contains(PersonMessages.NameTooLong, result.Errors.Select(x => x.ErrorMessage));
    }

    [UnitFact]
    public async Task GivenEmptyEmail_WhenValidating_ThenReturnsEmailRequired()
    {
        // Given
        var validator = new UpdatePersonCommandValidator();
        var command = Valid();
        command.Email = string.Empty;

        // When
        var result = await validator.ValidateAsync(command);

        // Then
        Assert.False(result.IsValid);
        Assert.Contains(PersonMessages.EmailRequired, result.Errors.Select(x => x.ErrorMessage));
    }

    [UnitFact]
    public async Task GivenMalformedEmail_WhenValidating_ThenReturnsEmailInvalid()
    {
        // Given
        var validator = new UpdatePersonCommandValidator();
        var command = Valid();
        command.Email = "not-an-email";

        // When
        var result = await validator.ValidateAsync(command);

        // Then
        Assert.False(result.IsValid);
        Assert.Contains(PersonMessages.EmailInvalid, result.Errors.Select(x => x.ErrorMessage));
    }
}
