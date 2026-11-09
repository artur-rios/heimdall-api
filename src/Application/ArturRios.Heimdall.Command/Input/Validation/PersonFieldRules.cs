using System.Net.Mail;
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
            .DeliverableEmail()
            .MaximumLength(256).WithMessage(PersonMessages.EmailTooLong);

    /// <summary>
    ///     An address Hermes can deliver to. Every email Heimdall sends goes through Hermes, so an address it refuses
    ///     belongs to someone who never gets their verification, reset or two-factor email. Applied where a person is
    ///     created or an address changes, and deliberately not at login or recovery, so an account created under the
    ///     looser rule is not locked out. The value is trimmed first because the handlers store it trimmed.
    /// </summary>
    public static IRuleBuilderOptions<T, string> DeliverableEmail<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(IsDeliverable).WithMessage(PersonMessages.EmailInvalid);

    /// <summary>
    ///     Hermes's <c>Mailbox.IsAddress</c>. A null is passed through for <c>NotEmpty</c> to report. The domain starts
    ///     after the first <c>@</c>, where Hermes reads from the last one: a bracketed literal holding an <c>@</c> is
    ///     then refused like any other literal.
    /// </summary>
    private static bool IsDeliverable(string? value)
    {
        if (value is null)
        {
            return true;
        }

        var address = value.Trim();

        return MailAddress.TryCreate(address, out var mailbox)
            && string.Equals(address, mailbox.Address, StringComparison.OrdinalIgnoreCase)
            && !address.Contains('"')
            && HasDeliverableDomain(address[(address.IndexOf('@') + 1)..]);
    }

    private static bool HasDeliverableDomain(string domain) =>
        domain.Contains('.') && !domain.EndsWith('.') && !domain.StartsWith('[');

    public static IRuleBuilderOptions<T, string> PersonPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().WithMessage(PersonMessages.PasswordRequired)
            .MinimumLength(8).WithMessage(PersonMessages.PasswordTooShort);
}
