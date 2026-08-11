using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>
///     Input validation for <see cref="SetGoogleSignInCommand" /> (UC-24). Only checks the shape of
///     the request — that the caller actually said which value to set; the business rules that
///     require data access (the scope exists, the actor owns it) are enforced by the handler.
/// </summary>
public class SetGoogleSignInCommandValidator : AbstractValidator<SetGoogleSignInCommand>
{
    public SetGoogleSignInCommandValidator()
    {
        RuleFor(command => command.Enabled)
            .NotNull()
            .WithMessage(ScopeMessages.EnabledRequired);
    }
}
