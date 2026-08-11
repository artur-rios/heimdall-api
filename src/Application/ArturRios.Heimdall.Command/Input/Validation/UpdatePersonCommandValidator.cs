using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="UpdatePersonCommand" /> (UC-08 step 2). Business rules that
///     need data access — existence, authorization, email uniqueness, scope ownership — are enforced
///     by the handler.
/// </summary>
public class UpdatePersonCommandValidator : AbstractValidator<UpdatePersonCommand>
{
    public UpdatePersonCommandValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty().WithMessage(PersonMessages.NameRequired)
            .MaximumLength(200).WithMessage(PersonMessages.NameTooLong);

        RuleFor(command => command.Email)
            .NotEmpty().WithMessage(PersonMessages.EmailRequired)
            .EmailAddress().WithMessage(PersonMessages.EmailInvalid);

        // The role is optional: null means "leave it unchanged". When supplied it must name one of
        // the three defined roles; whether the transition is *allowed* is the handler's decision.
        RuleFor(command => command.RoleId)
            .Must(role => role is (int)Roles.SystemAdmin or (int)Roles.ScopeAdmin or (int)Roles.User)
            .When(command => command.RoleId is not null)
            .WithMessage(PersonMessages.UnknownRole);
    }
}
