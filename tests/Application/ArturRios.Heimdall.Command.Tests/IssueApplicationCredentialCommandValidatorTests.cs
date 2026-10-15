using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for IssueApplicationCredentialCommandValidator (UC-53 AF-53c): no expiry is valid, a
// future one is valid, and one at or before now is refused — read as UTC when it carries no offset.
public class IssueApplicationCredentialCommandValidatorTests
{
    private static readonly IssueApplicationCredentialCommandValidator Validator = new();

    [UnitFact]
    public async Task GivenNoExpiry_WhenValidating_ThenItIsValid()
    {
        // Given
        var command = new IssueApplicationCredentialCommand();

        // When
        var result = await Validator.ValidateAsync(command);

        // Then
        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenAFutureExpiry_WhenValidating_ThenItIsValid()
    {
        // Given
        var command = new IssueApplicationCredentialCommand { ExpiresAt = DateTime.UtcNow.AddDays(30) };

        // When
        var result = await Validator.ValidateAsync(command);

        // Then
        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenAPastExpiry_WhenValidating_ThenItIsRefused()
    {
        // Given
        var command = new IssueApplicationCredentialCommand { ExpiresAt = DateTime.UtcNow.AddMinutes(-1) };

        // When
        var result = await Validator.ValidateAsync(command);

        // Then
        Assert.Equal([ApplicationCredentialMessages.ExpiryNotInFuture], result.Errors.Select(e => e.ErrorMessage));
    }

    [UnitFact]
    public async Task GivenAnExpiryWithoutAnOffset_WhenValidating_ThenItIsReadAsUtc()
    {
        // Given — one hour ago in UTC, with no kind; read as local time on a machine east of UTC it
        // would be in the future
        var past = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(-1), DateTimeKind.Unspecified);
        var command = new IssueApplicationCredentialCommand { ExpiresAt = past };

        // When
        var result = await Validator.ValidateAsync(command);

        // Then
        Assert.False(result.IsValid);
    }
}
