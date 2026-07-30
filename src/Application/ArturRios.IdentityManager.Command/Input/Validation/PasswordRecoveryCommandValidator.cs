using ArturRios.IdentityManager.Shared.Messages;
using FluentValidation;

namespace ArturRios.IdentityManager.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="PasswordRecoveryCommand" /> (UC-12, NFR-10). Whether the
///     address belongs to anyone is the handler's business, and it answers the same way either way
///     (AF-12a).
/// </summary>
/// <remarks>
///     UC-12 lists no validation alternative flow, but NFR-10 requires every input to be validated,
///     so a missing or malformed address is refused as a bad request before any lookup runs. That
///     costs nothing in enumeration terms: a caller learns their own address is malformed, never
///     whether it is registered.
/// </remarks>
public class PasswordRecoveryCommandValidator : AbstractValidator<PasswordRecoveryCommand>
{
    public PasswordRecoveryCommandValidator()
    {
        RuleFor(command => command.Email)
            .NotEmpty().WithMessage(AuthMessages.EmailRequired)
            .EmailAddress().WithMessage(AuthMessages.EmailInvalid);
    }
}
