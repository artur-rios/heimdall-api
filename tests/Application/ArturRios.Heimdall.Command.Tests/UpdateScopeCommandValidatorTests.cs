using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using FluentValidation.TestHelper;

namespace ArturRios.Heimdall.Command.Tests;

public class UpdateScopeCommandValidatorTests
{
    private readonly UpdateScopeCommandValidator _validator = new();

    [UnitFact]
    public void GivenEmptyName_WhenValidating_ThenNameRequiredError()
    {
        // Given
        var command = new UpdateScopeCommand { Id = Guid.NewGuid(), Name = string.Empty };

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldHaveValidationErrorFor(x => x.Name).WithErrorMessage(ScopeMessages.NameRequired);
    }

    [UnitFact]
    public void GivenNonEmptyName_WhenValidating_ThenNoNameError()
    {
        // Given
        var command = new UpdateScopeCommand { Id = Guid.NewGuid(), Name = "Acme" };

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldNotHaveValidationErrorFor(x => x.Name);
    }
}
