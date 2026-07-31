using ArturRios.IdentityManager.Shared.Messages;
using FluentValidation;

namespace ArturRios.IdentityManager.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="CreateApplicationCommand" /> (UC-16, AF-16d). Scope existence,
///     actor authorization, and owner eligibility are enforced by the handler.
/// </summary>
public class CreateApplicationCommandValidator : AbstractValidator<CreateApplicationCommand>
{
    public CreateApplicationCommandValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty().WithMessage(ApplicationMessages.NameRequired)
            .MaximumLength(200).WithMessage(ApplicationMessages.NameTooLong);

        RuleFor(command => command.OwnerId)
            .NotEmpty().WithMessage(ApplicationMessages.OwnerRequired);
    }
}
