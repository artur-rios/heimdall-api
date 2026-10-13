namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Delivers a password reset token to a person's address (UC-12, FR-PR-02). The concrete
///     delivery mechanism is an infrastructure concern.
/// </summary>
public interface IPasswordResetSender
{
    /// <param name="email">The address to deliver to.</param>
    /// <param name="token">The single-use token the link carries.</param>
    /// <param name="linkBase">
    ///     The recipient scope's own password-reset link base, or <c>null</c> to use the global setting.
    /// </param>
    Task SendAsync(string email, string token, string? linkBase);
}
