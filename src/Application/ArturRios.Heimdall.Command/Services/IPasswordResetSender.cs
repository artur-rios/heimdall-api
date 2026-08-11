namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Delivers a password reset token to a person's address (UC-12, FR-PR-02). The concrete
///     delivery mechanism is an infrastructure concern.
/// </summary>
public interface IPasswordResetSender
{
    Task SendAsync(string email, string token);
}
