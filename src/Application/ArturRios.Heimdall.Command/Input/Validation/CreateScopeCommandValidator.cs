using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>
///     Input validation for <see cref="CreateScopeCommand" /> (UC-01, step 2 / AF-01b). Only checks
///     the shape of the request — including the field lengths the columns impose, so an overlong
///     value is a named 400 here rather than the persistence layer's unclassified data-access
///     failure; business rules that require data access (name uniqueness, owner eligibility) are
///     enforced by the handler.
/// </summary>
public class CreateScopeCommandValidator : AbstractValidator<CreateScopeCommand>
{
    public CreateScopeCommandValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .WithMessage(ScopeMessages.NameRequired)
            .MaximumLength(200)
            .WithMessage(ScopeMessages.NameTooLong);

        // FluentValidation's MaximumLength rule skips a null argument, so only a non-null overlong
        // description is rejected — matching how the Application and ScopePermission validators
        // treat their optional descriptions.
        RuleFor(command => command.Description)
            .MaximumLength(500)
            .WithMessage(ScopeMessages.DescriptionTooLong);

        RuleFor(command => command.OwnerIds)
            .NotEmpty()
            .WithMessage(ScopeMessages.AtLeastOneOwnerRequired);
    }
}
