using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using FluentValidation.TestHelper;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for CreateScopePermissionCommandValidator (UC-31 AF-31d): the shape rules only. Scope
// existence and actor authorization live in CreateScopePermissionCommandHandlerTests.
public class CreateScopePermissionCommandValidatorTests
{
    private readonly CreateScopePermissionCommandValidator _validator = new();

    private static CreateScopePermissionCommand Command(string name, string? description = "docs") => new()
    {
        ScopeId = Guid.NewGuid(),
        Name = name,
        Description = description,
        IncludeAsJwtClaim = true
    };

    [UnitFact]
    public void GivenValidCommand_WhenValidating_ThenNoErrors()
    {
        // Given
        var command = Command("billing:read");

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldNotHaveAnyValidationErrors();
    }

    [UnitFact]
    public void GivenEmptyName_WhenValidating_ThenNameRequiredIsReported()
    {
        // Given
        var command = Command(string.Empty);

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage(ScopePermissionMessages.NameRequired);
    }

    [UnitFact]
    public void GivenNameOf201Characters_WhenValidating_ThenNameTooLongIsReported()
    {
        // Given a name one character past the maximum
        var command = Command(new string('a', 201));

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage(ScopePermissionMessages.NameTooLong);
    }

    [UnitFact]
    public void GivenNameOf200Characters_WhenValidating_ThenNoErrors()
    {
        // Given a name exactly at the maximum
        var command = Command(new string('a', 200));

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldNotHaveValidationErrorFor(x => x.Name);
    }

    [UnitFact]
    public void GivenDescriptionOf501Characters_WhenValidating_ThenDescriptionTooLongIsReported()
    {
        // Given a description one character past the maximum
        var command = Command("billing:read", new string('a', 501));

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldHaveValidationErrorFor(x => x.Description)
            .WithErrorMessage(ScopePermissionMessages.DescriptionTooLong);
    }

    [UnitFact]
    public void GivenDescriptionOf500Characters_WhenValidating_ThenNoErrors()
    {
        // Given a description exactly at the maximum
        var command = Command("billing:read", new string('a', 500));

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldNotHaveValidationErrorFor(x => x.Description);
    }

    [UnitFact]
    public void GivenNullDescription_WhenValidating_ThenNoErrors()
    {
        // Given a null description — the field is optional
        var command = Command("billing:read", null);

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldNotHaveValidationErrorFor(x => x.Description);
    }
}