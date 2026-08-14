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

    [UnitFact]
    public void GivenNameOf201Characters_WhenValidating_ThenNameTooLongIsReported()
    {
        // Given a name one character past the column that stores it. Without this rule the value
        // reached PostgreSQL and came back as the persistence layer's data-access failure rather
        // than naming the field (NFR-10).
        var command = new UpdateScopeCommand { Id = Guid.NewGuid(), Name = new string('a', 201) };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Name).WithErrorMessage(ScopeMessages.NameTooLong);
    }

    [UnitFact]
    public void GivenNameOf200Characters_WhenValidating_ThenNoNameError()
    {
        var command = new UpdateScopeCommand { Id = Guid.NewGuid(), Name = new string('a', 200) };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveValidationErrorFor(x => x.Name);
    }

    [UnitFact]
    public void GivenDescriptionOf501Characters_WhenValidating_ThenDescriptionTooLongIsReported()
    {
        var command = new UpdateScopeCommand
        {
            Id = Guid.NewGuid(), Name = "Acme", Description = new string('a', 501)
        };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Description)
            .WithErrorMessage(ScopeMessages.DescriptionTooLong);
    }

    [UnitFact]
    public void GivenNullDescription_WhenValidating_ThenNoDescriptionError()
    {
        var command = new UpdateScopeCommand { Id = Guid.NewGuid(), Name = "Acme", Description = null };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveValidationErrorFor(x => x.Description);
    }
}
