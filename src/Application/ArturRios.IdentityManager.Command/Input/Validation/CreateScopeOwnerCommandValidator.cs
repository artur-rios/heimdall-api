using ArturRios.IdentityManager.Shared.Messages;
using FluentValidation;

namespace ArturRios.IdentityManager.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="CreateScopeOwnerCommand" /> (UC-06 path c, AF-06d). Scope
///     existence, ownership, and email uniqueness are enforced by the handler.
/// </summary>
public class CreateScopeOwnerCommandValidator : AbstractValidator<CreateScopeOwnerCommand>
{
    public CreateScopeOwnerCommandValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty().WithMessage(PersonMessages.NameRequired)
            .MaximumLength(200).WithMessage(PersonMessages.NameTooLong);

        RuleFor(command => command.Email)
            .NotEmpty().WithMessage(PersonMessages.EmailRequired)
            .EmailAddress().WithMessage(PersonMessages.EmailInvalid);

        RuleFor(command => command.Password)
            .NotEmpty().WithMessage(PersonMessages.PasswordRequired)
            .MinimumLength(8).WithMessage(PersonMessages.PasswordTooShort);
    }
}
