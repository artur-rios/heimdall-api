using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for IssueApplicationTokenCommandValidator (UC-56 AF-56a): the three fields are required
// and the secret is bounded. Nothing here looks at whether the secret is right — that is AF-56b's
// single refusal, so a malformed request is the only thing a 400 can say.
public class IssueApplicationTokenCommandValidatorTests
{
    private static readonly IssueApplicationTokenCommandValidator Validator = new();

    private static IssueApplicationTokenCommand Valid() => new()
    {
        ApplicationId = Guid.NewGuid(), ScopeId = Guid.NewGuid(), Secret = "hds_" + new string('a', 43)
    };

    [UnitFact]
    public async Task GivenAllFields_WhenValidating_ThenItIsValid()
    {
        // Given / When
        var result = await Validator.ValidateAsync(Valid());

        // Then
        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenNothing_WhenValidating_ThenEachMissingFieldIsNamed()
    {
        // Given / When
        var result = await Validator.ValidateAsync(new IssueApplicationTokenCommand());

        // Then
        Assert.Equal(
            [ApplicationTokenMessages.ApplicationIdRequired, ApplicationTokenMessages.ScopeIdRequired, ApplicationTokenMessages.SecretRequired],
            result.Errors.Select(e => e.ErrorMessage));
    }

    [UnitFact]
    public async Task GivenAnOverlongSecret_WhenValidating_ThenItIsRefused()
    {
        // Given
        var command = Valid();
        command.Secret = new string('a', 101);

        // When
        var result = await Validator.ValidateAsync(command);

        // Then
        Assert.Equal([ApplicationTokenMessages.SecretTooLong], result.Errors.Select(e => e.ErrorMessage));
    }
}
