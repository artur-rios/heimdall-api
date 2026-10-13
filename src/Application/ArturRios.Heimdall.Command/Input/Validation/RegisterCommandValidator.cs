using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="RegisterCommand" /> (UC-47, FR-SR-03). A caller learns only that
///     their own input is malformed — never whether an address is registered.
/// </summary>
public class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
    {
        RuleFor(command => command.Name).PersonName();
        RuleFor(command => command.Email).PersonEmail();
        RuleFor(command => command.Password).PersonPassword();
        RuleFor(command => command.ScopeId).NotNull().WithMessage(AuthMessages.ScopeRequired);
    }
}
