using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for VerifyEmailCommandValidator (UC-14, NFR-10). The command carries one field, so
// there is one rule: a token must be supplied. Whether it is real, expired, or spent belongs to the
// handler (AF-14a…AF-14c).
public class VerifyEmailCommandValidatorTests
{
    private static VerifyEmailCommand Valid() =>
        new() { Token = "gG7pQ2mZ4kR9xT1vB6nL8sC3wD5yF0hJ7aE2uI4oP6qS" };

    [UnitFact]
    public async Task GivenValidCommand_WhenValidating_ThenNoErrors()
    {
        // Given / When
        var result = await new VerifyEmailCommandValidator().ValidateAsync(Valid());

        // Then
        Assert.True(result.IsValid);
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GivenNoToken_WhenValidating_ThenTokenRequiredError(string token)
    {
        // Given a request carrying nothing to verify with
        var command = Valid();
        command.Token = token;

        // When
        var result = await new VerifyEmailCommandValidator().ValidateAsync(command);

        // Then
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.ErrorMessage == AuthMessages.TokenRequired);
    }

    [UnitFact]
    public async Task GivenUnknownButWellFormedToken_WhenValidating_ThenNoErrors()
    {
        // Given a token belonging to nobody. Whether it exists is AF-14c's business, decided in the
        // handler — the validator must not pre-empt that by rejecting the shape.
        var command = Valid();
        command.Token = "not-a-token-anybody-was-ever-issued";

        // When
        var result = await new VerifyEmailCommandValidator().ValidateAsync(command);

        // Then
        Assert.True(result.IsValid);
    }
}
