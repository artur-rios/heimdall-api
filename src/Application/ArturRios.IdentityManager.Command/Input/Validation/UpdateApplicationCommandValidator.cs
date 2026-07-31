using ArturRios.IdentityManager.Shared.Messages;
using FluentValidation;

namespace ArturRios.IdentityManager.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="UpdateApplicationCommand" /> (UC-18 main flow step 2). The
///     messages are UC-16's: the two use cases reject the same shapes, and UC-18 defines no
///     alternative flow of its own for invalid input. Application existence, actor authorization, and
///     owner eligibility are enforced by the handler.
/// </summary>
public class UpdateApplicationCommandValidator : AbstractValidator<UpdateApplicationCommand>
{
    public UpdateApplicationCommandValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty().WithMessage(ApplicationMessages.NameRequired)
            .MaximumLength(200).WithMessage(ApplicationMessages.NameTooLong);

        RuleFor(command => command.OwnerId)
            .NotEmpty().WithMessage(ApplicationMessages.OwnerRequired);
    }
}
