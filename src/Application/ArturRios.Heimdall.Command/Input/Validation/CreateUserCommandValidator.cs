using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="CreateUserCommand" /> (UC-06 path a, AF-06d). Scope existence,
///     ownership, and email uniqueness are enforced by the handler.
/// </summary>
public class CreateUserCommandValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserCommandValidator()
    {
        RuleFor(command => command.Name).PersonName();
        RuleFor(command => command.Email).PersonEmail();
        RuleFor(command => command.Password).PersonPassword();
    }
}
