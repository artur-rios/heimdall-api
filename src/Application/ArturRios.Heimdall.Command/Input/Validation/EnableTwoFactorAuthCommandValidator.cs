using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="EnableTwoFactorAuthCommand" /> (UC-36, AF-36c): at least one
///     of <c>"App"</c>/<c>"Email"</c> must be selected, and nothing else. Eligibility (AF-36b) and
///     the already-active check (AF-36a) are enforced by the handler, since both need a database
///     read the validator has no business making.
/// </summary>
public class EnableTwoFactorAuthCommandValidator : AbstractValidator<EnableTwoFactorAuthCommand>
{
    private static readonly string[] KnownMethods = ["App", "Email"];

    public EnableTwoFactorAuthCommandValidator()
    {
        RuleFor(command => command.Methods)
            .Must(methods => methods is { Count: > 0 } &&
                              methods.All(method => KnownMethods.Contains(method, StringComparer.OrdinalIgnoreCase)))
            .WithMessage(TwoFactorMessages.NoMethodSelected);
    }
}
