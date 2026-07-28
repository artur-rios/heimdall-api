using ArturRios.IdentityManager.Shared.Messages;
using FluentValidation;

namespace ArturRios.IdentityManager.Command.Input.Validation;

/// <summary>
///     Input validation for <see cref="UpdateScopeCommand" /> (UC-03). Only checks the shape of the
///     request; business rules that require data access (existence, name uniqueness) are enforced by
///     the handler.
/// </summary>
public class UpdateScopeCommandValidator : AbstractValidator<UpdateScopeCommand>
{
    public UpdateScopeCommandValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .WithMessage(ScopeMessages.NameRequired);
    }
}
