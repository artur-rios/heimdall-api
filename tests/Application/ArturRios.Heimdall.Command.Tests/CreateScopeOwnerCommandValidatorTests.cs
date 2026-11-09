using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for CreateScopeOwnerCommandValidator (UC-06 path c, AF-06d).
public class CreateScopeOwnerCommandValidatorTests
{
    private static CreateScopeOwnerCommand Valid() => new()
    {
        Name = "Owner", Email = "owner@test.local", Password = "Str0ngPass!"
    };

    [UnitFact]
    public async Task GivenValidCommand_WhenValidating_ThenNoErrors()
    {
        // Given / When
        var result = await new CreateScopeOwnerCommandValidator().ValidateAsync(Valid());

        // Then
        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenAnAddressHermesCannotDeliverTo_WhenValidating_ThenEmailInvalidError()
    {
        // Given
        var command = Valid();
        command.Email = "owner@undotted";

        // When
        var result = await new CreateScopeOwnerCommandValidator().ValidateAsync(command);

        // Then
        Assert.Contains(result.Errors, error => error.ErrorMessage == PersonMessages.EmailInvalid);
    }
}
