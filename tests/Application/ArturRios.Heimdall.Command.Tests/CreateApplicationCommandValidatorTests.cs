using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using FluentValidation.TestHelper;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for CreateApplicationCommandValidator (UC-16 AF-16d): the shape rules only. Scope
// existence, actor authorization, and owner eligibility live in CreateApplicationCommandHandlerTests.
public class CreateApplicationCommandValidatorTests
{
    private readonly CreateApplicationCommandValidator _validator = new();

    private static CreateApplicationCommand Command(string name, Guid ownerId) => new()
    {
        ScopeId = Guid.NewGuid(), Name = name, OwnerId = ownerId
    };

    [UnitFact]
    public void GivenValidCommand_WhenValidating_ThenNoErrors()
    {
        // Given
        var command = Command("Billing Service", Guid.NewGuid());

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldNotHaveAnyValidationErrors();
    }

    [UnitFact]
    public void GivenEmptyName_WhenValidating_ThenNameRequiredIsReported()
    {
        // Given
        var command = Command(string.Empty, Guid.NewGuid());

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage(ApplicationMessages.NameRequired);
    }

    [UnitFact]
    public void GivenNameOf201Characters_WhenValidating_ThenNameTooLongIsReported()
    {
        // Given a name one character past the FR-AP-01 maximum
        var command = Command(new string('a', 201), Guid.NewGuid());

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage(ApplicationMessages.NameTooLong);
    }

    [UnitFact]
    public void GivenNameOf200Characters_WhenValidating_ThenNoErrors()
    {
        // Given a name exactly at the maximum
        var command = Command(new string('a', 200), Guid.NewGuid());

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldNotHaveValidationErrorFor(x => x.Name);
    }

    [UnitFact]
    public void GivenEmptyOwnerId_WhenValidating_ThenOwnerRequiredIsReported()
    {
        // Given no owner (FR-AP-03 requires exactly one)
        var command = Command("Billing Service", Guid.Empty);

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldHaveValidationErrorFor(x => x.OwnerId)
            .WithErrorMessage(ApplicationMessages.OwnerRequired);
    }
}
