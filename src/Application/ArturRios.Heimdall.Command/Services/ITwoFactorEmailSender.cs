namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Delivers a two-factor email code to a person's address (UC-36 step 4, FR-2F-03). The concrete
///     delivery mechanism is an infrastructure concern, following the same split as
///     <see cref="IPasswordResetSender" /> and <see cref="IEmailVerificationSender" />.
/// </summary>
public interface ITwoFactorEmailSender
{
    Task SendAsync(string email, string code);
}
