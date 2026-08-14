using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using FluentValidation.TestHelper;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for CreateScopeCommandValidator (UC-01 step 2 / AF-01b): the name is required and
// bounded, the description is optional and bounded, and at least one owner must be named. Name
// uniqueness and owner eligibility need data access and belong to the handler.
//
// The length rules are the reason this file exists. Every other entity's validator bounded its
// fields and this one did not, so an overlong value reached PostgreSQL and returned the persistence
// layer's data-access failure instead of saying which field was too long (NFR-10).
public class CreateScopeCommandValidatorTests
{
    private readonly CreateScopeCommandValidator _validator = new();

    private static CreateScopeCommand Command(string? name = "Acme", string? description = null) => new()
    {
        Name = name ?? string.Empty,
        Description = description,
        OwnerIds = [Guid.NewGuid()]
    };

    [UnitFact]
    public void GivenValidCommand_WhenValidating_ThenNoErrors()
    {
        var result = _validator.TestValidate(Command());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [UnitFact]
    public void GivenEmptyName_WhenValidating_ThenNameRequiredIsReported()
    {
        var result = _validator.TestValidate(Command(name: string.Empty));

        result.ShouldHaveValidationErrorFor(command => command.Name)
            .WithErrorMessage(ScopeMessages.NameRequired);
    }

    [UnitFact]
    public void GivenNameOf201Characters_WhenValidating_ThenNameTooLongIsReported()
    {
        // Given a name one character past the column that stores it
        var result = _validator.TestValidate(Command(name: new string('a', 201)));

        result.ShouldHaveValidationErrorFor(command => command.Name)
            .WithErrorMessage(ScopeMessages.NameTooLong);
    }

    [UnitFact]
    public void GivenNameOf200Characters_WhenValidating_ThenNoErrors()
    {
        // Given a name exactly at the maximum — the boundary belongs in the accepted set
        var result = _validator.TestValidate(Command(name: new string('a', 200)));

        result.ShouldNotHaveValidationErrorFor(command => command.Name);
    }

    [UnitFact]
    public void GivenDescriptionOf501Characters_WhenValidating_ThenDescriptionTooLongIsReported()
    {
        var result = _validator.TestValidate(Command(description: new string('a', 501)));

        result.ShouldHaveValidationErrorFor(command => command.Description)
            .WithErrorMessage(ScopeMessages.DescriptionTooLong);
    }

    [UnitFact]
    public void GivenDescriptionOf500Characters_WhenValidating_ThenNoErrors()
    {
        var result = _validator.TestValidate(Command(description: new string('a', 500)));

        result.ShouldNotHaveValidationErrorFor(command => command.Description);
    }

    [UnitFact]
    public void GivenNullDescription_WhenValidating_ThenNoErrors()
    {
        // The description is optional, and FluentValidation's MaximumLength skips a null argument —
        // pinned so the rule cannot start rejecting the ordinary case.
        var result = _validator.TestValidate(Command(description: null));

        result.ShouldNotHaveValidationErrorFor(command => command.Description);
    }

    [UnitFact]
    public void GivenNoOwner_WhenValidating_ThenAtLeastOneOwnerRequiredIsReported()
    {
        // NFR-12 in its first form: a scope is never created without an owner, so it can never begin
        // life in the state UC-09, UC-10 and UC-22 all refuse to leave it in.
        var command = Command();
        command.OwnerIds = [];

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.OwnerIds)
            .WithErrorMessage(ScopeMessages.AtLeastOneOwnerRequired);
    }
}
