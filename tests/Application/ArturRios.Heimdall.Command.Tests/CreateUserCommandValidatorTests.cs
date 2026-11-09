using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for CreateUserCommandValidator (UC-06 path a, AF-06d).
public class CreateUserCommandValidatorTests
{
    private static CreateUserCommand Valid() => new()
    {
        Name = "User", Email = "user@test.local", Password = "Str0ngPass!"
    };

    [UnitFact]
    public async Task GivenValidCommand_WhenValidating_ThenNoErrors()
    {
        // Given / When
        var result = await new CreateUserCommandValidator().ValidateAsync(Valid());

        // Then
        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenAnAddressHermesCannotDeliverTo_WhenValidating_ThenEmailInvalidError()
    {
        // Given
        var command = Valid();
        command.Email = "user@undotted";

        // When
        var result = await new CreateUserCommandValidator().ValidateAsync(command);

        // Then
        Assert.Contains(result.Errors, error => error.ErrorMessage == PersonMessages.EmailInvalid);
    }
}
