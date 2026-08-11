using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="UpdateScopePermissionCommand" /> (UC-33 main flow step 2).
///     The messages are UC-31's: the two use cases reject the same shapes, and UC-33 defines no
///     alternative flow of its own for invalid input. Permission existence and actor authorization
///     are enforced by the handler. A <c>null</c> description is valid: FluentValidation's
///     <c>MaximumLength</c> rule skips <c>null</c> arguments, so only a non-null overlong description
///     is rejected.
/// </summary>
public class UpdateScopePermissionCommandValidator : AbstractValidator<UpdateScopePermissionCommand>
{
    public UpdateScopePermissionCommandValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty().WithMessage(ScopePermissionMessages.NameRequired)
            .MaximumLength(200).WithMessage(ScopePermissionMessages.NameTooLong);

        RuleFor(command => command.Description)
            .MaximumLength(500).WithMessage(ScopePermissionMessages.DescriptionTooLong);
    }
}