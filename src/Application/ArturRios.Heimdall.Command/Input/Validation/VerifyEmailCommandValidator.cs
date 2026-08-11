using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="VerifyEmailCommand" /> (UC-14). Whether the token exists, has
///     expired, or has been used is the handler's business (AF-14a…AF-14c).
/// </summary>
/// <remarks>
///     UC-14 defines no alternative flow for a malformed request — unlike UC-13's AF-13d — but NFR-10
///     requires every input to be validated. Nothing is contradicted by the rule: an absent token
///     answers 400 here, and would answer 400 as AF-14c if it reached the lookup.
/// </remarks>
public class VerifyEmailCommandValidator : AbstractValidator<VerifyEmailCommand>
{
    public VerifyEmailCommandValidator()
    {
        RuleFor(command => command.Token)
            .NotEmpty().WithMessage(AuthMessages.TokenRequired);
    }
}
