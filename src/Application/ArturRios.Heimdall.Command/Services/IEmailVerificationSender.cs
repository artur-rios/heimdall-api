namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Delivers an email-verification token to a person's address (UC-06 / FR-EV-01). The concrete
///     delivery mechanism is an infrastructure concern; UC-06 ships a logging stub.
/// </summary>
public interface IEmailVerificationSender
{
    /// <param name="email">The address to deliver to.</param>
    /// <param name="token">The single-use token the link carries.</param>
    /// <param name="linkBase">
    ///     The recipient scope's own verification link base, or <c>null</c> to use the global setting.
    /// </param>
    Task SendAsync(string email, string token, string? linkBase);
}
