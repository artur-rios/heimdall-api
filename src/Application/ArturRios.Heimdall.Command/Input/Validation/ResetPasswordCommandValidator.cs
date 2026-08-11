using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="ResetPasswordCommand" /> (UC-13, AF-13d). Whether the token
///     exists, has expired, or has been used is the handler's business (AF-13a…AF-13c).
/// </summary>
/// <remarks>
///     The minimum length is the one UC-06 applies when a person is created: a reset must not be a
///     way around the floor that person creation enforces. No maximum is imposed — the password is
///     hashed, never stored — and no composition rules are added, since the specification defines
///     none.
/// </remarks>
public class ResetPasswordCommandValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordCommandValidator()
    {
        RuleFor(command => command.Token)
            .NotEmpty().WithMessage(AuthMessages.TokenRequired);

        RuleFor(command => command.NewPassword)
            .NotEmpty().WithMessage(AuthMessages.PasswordRequired)
            .MinimumLength(8).WithMessage(AuthMessages.PasswordTooShort);
    }
}
