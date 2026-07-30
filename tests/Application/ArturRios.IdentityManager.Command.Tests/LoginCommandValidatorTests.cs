using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Input.Validation;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for LoginCommandValidator (UC-11, AF-11f).
public class LoginCommandValidatorTests
{
    private static LoginCommand Valid() => new()
    {
        Email = "person@test.local",
        Password = "Str0ngPass!"
    };

    [UnitFact]
    public async Task GivenValidCommand_WhenValidating_ThenNoErrors()
    {
        // Given / When
        var result = await new LoginCommandValidator().ValidateAsync(Valid());

        // Then
        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenScopeIdSupplied_WhenValidating_ThenNoErrors()
    {
        // Given a User's login, which also names a scope
        var command = Valid();
        command.ScopeId = Guid.NewGuid();

        // When
        var result = await new LoginCommandValidator().ValidateAsync(command);

        // Then
        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenEmptyEmail_WhenValidating_ThenEmailRequiredError()
    {
        // Given
        var command = Valid();
        command.Email = "";

        // When
        var result = await new LoginCommandValidator().ValidateAsync(command);

        // Then
        Assert.Contains(result.Errors, error => error.ErrorMessage == AuthMessages.EmailRequired);
    }

    [UnitFact]
    public async Task GivenMalformedEmail_WhenValidating_ThenEmailInvalidError()
    {
        // Given
        var command = Valid();
        command.Email = "not-an-email";

        // When
        var result = await new LoginCommandValidator().ValidateAsync(command);

        // Then
        Assert.Contains(result.Errors, error => error.ErrorMessage == AuthMessages.EmailInvalid);
    }

    [UnitFact]
    public async Task GivenEmptyPassword_WhenValidating_ThenPasswordRequiredError()
    {
        // Given
        var command = Valid();
        command.Password = "";

        // When
        var result = await new LoginCommandValidator().ValidateAsync(command);

        // Then
        Assert.Contains(result.Errors, error => error.ErrorMessage == AuthMessages.PasswordRequired);
    }

    [UnitFact]
    public async Task GivenShortPassword_WhenValidating_ThenNoErrors()
    {
        // Given a password too short to be anyone's: at login that is a wrong password (401), not a
        // malformed request, so the validator must let it through to the handler.
        var command = Valid();
        command.Password = "short";

        // When
        var result = await new LoginCommandValidator().ValidateAsync(command);

        // Then
        Assert.True(result.IsValid);
    }
}
