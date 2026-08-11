using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="CreateAdminCommand" /> (UC-06 path b, AF-06d). Business rules
///     (email uniqueness) are enforced by the handler.
/// </summary>
public class CreateAdminCommandValidator : AbstractValidator<CreateAdminCommand>
{
    public CreateAdminCommandValidator()
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

        RuleFor(command => command.Role)
            .Must(role => role == (int)Roles.SystemAdmin || role == (int)Roles.ScopeAdmin)
            .WithMessage(PersonMessages.InvalidRole);
    }
}
