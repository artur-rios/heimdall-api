using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for ResetPasswordCommandValidator (UC-13, AF-13d). Whether the token is real, expired,
// or spent belongs to the handler (AF-13a…AF-13c); these pin only the shape, and in particular the
// eight-character floor that stops a reset being a way around UC-06's rule.
public class ResetPasswordCommandValidatorTests
{
    private static ResetPasswordCommand Valid() => new()
    {
        Token = "gG7pQ2mZ4kR9xT1vB6nL8sC3wD5yF0hJ7aE2uI4oP6qS",
        NewPassword = "Str0ng-New-Pass!"
    };

    [UnitFact]
    public async Task GivenValidCommand_WhenValidating_ThenNoErrors()
    {
        // Given / When
        var result = await new ResetPasswordCommandValidator().ValidateAsync(Valid());

        // Then
        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenEmptyToken_WhenValidating_ThenTokenRequiredError()
    {
        // Given
        var command = Valid();
        command.Token = "";

        // When
        var result = await new ResetPasswordCommandValidator().ValidateAsync(command);

        // Then
        Assert.Contains(result.Errors, error => error.ErrorMessage == AuthMessages.TokenRequired);
    }

    [UnitFact]
    public async Task GivenEmptyPassword_WhenValidating_ThenPasswordRequiredError()
    {
        // Given
        var command = Valid();
        command.NewPassword = "";

        // When
        var result = await new ResetPasswordCommandValidator().ValidateAsync(command);

        // Then
        Assert.Contains(result.Errors, error => error.ErrorMessage == AuthMessages.PasswordRequired);
    }

    [UnitTheory]
    [InlineData("a")]
    [InlineData("Str0ng")]
    [InlineData("Str0ng!")]
    public async Task GivenPasswordShorterThanEightCharacters_WhenValidating_ThenPasswordTooShortError(
        string password)
    {
        // Given passwords below the floor UC-06 applies at person creation
        var command = Valid();
        command.NewPassword = password;

        // When
        var result = await new ResetPasswordCommandValidator().ValidateAsync(command);

        // Then
        Assert.Contains(result.Errors, error => error.ErrorMessage == AuthMessages.PasswordTooShort);
    }

    [UnitFact]
    public async Task GivenPasswordOfExactlyEightCharacters_WhenValidating_ThenNoErrors()
    {
        // Given the boundary of the rule above
        var command = Valid();
        command.NewPassword = "Str0ng!8";

        // When
        var result = await new ResetPasswordCommandValidator().ValidateAsync(command);

        // Then
        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenUnknownButWellFormedToken_WhenValidating_ThenNoErrors()
    {
        // Given a token belonging to nobody. Whether it exists is AF-13c's business, decided in the
        // handler — the validator must not pre-empt that by rejecting the shape.
        var command = Valid();
        command.Token = "not-a-token-anybody-was-ever-issued";

        // When
        var result = await new ResetPasswordCommandValidator().ValidateAsync(command);

        // Then
        Assert.True(result.IsValid);
    }
}
