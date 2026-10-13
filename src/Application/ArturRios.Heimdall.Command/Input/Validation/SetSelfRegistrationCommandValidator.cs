using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>Shape validation for <see cref="SetSelfRegistrationCommand" /> (UC-47 toggle).</summary>
public class SetSelfRegistrationCommandValidator : AbstractValidator<SetSelfRegistrationCommand>
{
    public SetSelfRegistrationCommandValidator()
    {
        RuleFor(command => command.Enabled)
            .NotNull()
            .WithMessage(ScopeMessages.EnabledRequired);
    }
}
