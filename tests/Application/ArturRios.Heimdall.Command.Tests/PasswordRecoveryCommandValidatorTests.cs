using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for PasswordRecoveryCommandValidator (UC-12, NFR-10). UC-12 lists no validation
// alternative flow, so these pin the deliberate choice to reject a malformed request rather than
// answer it with the generic success.
public class PasswordRecoveryCommandValidatorTests
{
    private static PasswordRecoveryCommand Valid() => new() { Email = "person@test.local" };

    [UnitFact]
    public async Task GivenValidCommand_WhenValidating_ThenNoErrors()
    {
        // Given / When
        var result = await new PasswordRecoveryCommandValidator().ValidateAsync(Valid());

        // Then
        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenScopeIdSupplied_WhenValidating_ThenNoErrors()
    {
        // Given a User's request, which also names a scope
        var command = Valid();
        command.ScopeId = Guid.NewGuid();

        // When
        var result = await new PasswordRecoveryCommandValidator().ValidateAsync(command);

        // Then
        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenNoScopeId_WhenValidating_ThenNoErrors()
    {
        // Given an admin's request. The scope is optional by design — its absence selects the
        // system-wide lookup rather than being a missing field.
        var command = Valid();
        command.ScopeId = null;

        // When
        var result = await new PasswordRecoveryCommandValidator().ValidateAsync(command);

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
        var result = await new PasswordRecoveryCommandValidator().ValidateAsync(command);

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
        var result = await new PasswordRecoveryCommandValidator().ValidateAsync(command);

        // Then
        Assert.Contains(result.Errors, error => error.ErrorMessage == AuthMessages.EmailInvalid);
    }

    [UnitFact]
    public async Task GivenUnregisteredButWellFormedEmail_WhenValidating_ThenNoErrors()
    {
        // Given an address that belongs to nobody. Whether it exists is AF-12a's business, decided
        // in the handler and never reported — the validator must not pre-empt that by rejecting it.
        var command = Valid();
        command.Email = "nobody@test.local";

        // When
        var result = await new PasswordRecoveryCommandValidator().ValidateAsync(command);

        // Then
        Assert.True(result.IsValid);
    }
}
