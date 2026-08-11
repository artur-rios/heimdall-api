using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>
///     Input validation for <see cref="CreateScopeCommand" /> (UC-01, step 2 / AF-01b). Only checks
///     the shape of the request; business rules that require data access (name uniqueness, owner
///     eligibility) are enforced by the handler.
/// </summary>
public class CreateScopeCommandValidator : AbstractValidator<CreateScopeCommand>
{
    public CreateScopeCommandValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .WithMessage(ScopeMessages.NameRequired);

        RuleFor(command => command.OwnerIds)
            .NotEmpty()
            .WithMessage(ScopeMessages.AtLeastOneOwnerRequired);
    }
}
