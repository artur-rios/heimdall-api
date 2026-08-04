using ArturRios.IdentityManager.Shared.Messages;
using FluentValidation;

namespace ArturRios.IdentityManager.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="CreateScopePermissionCommand" /> (UC-31, AF-31d). Scope
///     existence and actor authorization are enforced by the handler. A <c>null</c> description is
///     valid: FluentValidation's <c>MaximumLength</c> rule skips <c>null</c> arguments, so only a
///     non-null overlong description is rejected.
/// </summary>
public class CreateScopePermissionCommandValidator : AbstractValidator<CreateScopePermissionCommand>
{
    public CreateScopePermissionCommandValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty().WithMessage(ScopePermissionMessages.NameRequired)
            .MaximumLength(200).WithMessage(ScopePermissionMessages.NameTooLong);

        RuleFor(command => command.Description)
            .MaximumLength(500).WithMessage(ScopePermissionMessages.DescriptionTooLong);
    }
}