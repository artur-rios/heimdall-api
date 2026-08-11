using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="LoginCommand" /> (UC-11 AF-11f, NFR-10). Whether the
///     credentials are <em>correct</em> is the handler's business, and every wrong answer there is a
///     single 401 (AF-11a…AF-11e).
/// </summary>
/// <remarks>
///     Deliberately no minimum password length, unlike the validators that guard person creation: at
///     login a short password is a failed attempt, not a malformed request, and answering 400 to it
///     would tell a caller their guess was too short to be anyone's password.
/// </remarks>
public class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(command => command.Email)
            .NotEmpty().WithMessage(AuthMessages.EmailRequired)
            .EmailAddress().WithMessage(AuthMessages.EmailInvalid);

        RuleFor(command => command.Password)
            .NotEmpty().WithMessage(AuthMessages.PasswordRequired);
    }
}
