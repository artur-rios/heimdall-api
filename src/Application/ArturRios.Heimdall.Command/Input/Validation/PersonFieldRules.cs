using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>
///     The field rules for a password person, shared by every command that creates one (UC-06 path a,
///     UC-47), so an administrator and a self-registering caller can never be held to different rules.
/// </summary>
public static class PersonFieldRules
{
    public static IRuleBuilderOptions<T, string> PersonName<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().WithMessage(PersonMessages.NameRequired)
            .MaximumLength(200).WithMessage(PersonMessages.NameTooLong);

    public static IRuleBuilderOptions<T, string> PersonEmail<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().WithMessage(PersonMessages.EmailRequired)
            .EmailAddress().WithMessage(PersonMessages.EmailInvalid);

    public static IRuleBuilderOptions<T, string> PersonPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().WithMessage(PersonMessages.PasswordRequired)
            .MinimumLength(8).WithMessage(PersonMessages.PasswordTooShort);
}
