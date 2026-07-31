using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Input.Validation;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using FluentValidation.TestHelper;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for UpdateApplicationCommandValidator (UC-18 main flow step 2): the shape rules only.
// UC-18 defines no alternative flow for invalid input, so these reuse UC-16's messages. Application
// existence, actor authorization, and owner eligibility live in UpdateApplicationCommandHandlerTests.
public class UpdateApplicationCommandValidatorTests
{
    private readonly UpdateApplicationCommandValidator _validator = new();

    private static UpdateApplicationCommand Command(string name, Guid ownerId) => new()
    {
        ScopeId = Guid.NewGuid(), Id = Guid.NewGuid(), Name = name, OwnerId = ownerId
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
        // Given no owner: a PUT replaces the owner, so an unchanged one is resubmitted rather than
        // omitted (FR-AP-03 requires exactly one)
        var command = Command("Billing Service", Guid.Empty);

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldHaveValidationErrorFor(x => x.OwnerId)
            .WithErrorMessage(ApplicationMessages.OwnerRequired);
    }
}
