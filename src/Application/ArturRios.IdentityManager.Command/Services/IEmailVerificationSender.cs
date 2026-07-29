namespace ArturRios.IdentityManager.Command.Services;

/// <summary>
///     Delivers an email-verification token to a person's address (UC-06 / FR-EV-01). The concrete
///     delivery mechanism is an infrastructure concern; UC-06 ships a logging stub.
/// </summary>
public interface IEmailVerificationSender
{
    Task SendAsync(string email, string token);
}
