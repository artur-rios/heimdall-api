namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Mails a registrant the link that finishes a self-registration (UC-47 step 6). It is a different
///     message from <see cref="IEmailVerificationSender" />: the registrant must also present the
///     password chosen at sign-up, and the text says so. The concrete delivery is an infrastructure
///     concern.
/// </summary>
public interface IRegistrationLinkSender
{
    /// <param name="email">The address that was submitted.</param>
    /// <param name="token">The single-use token the link carries; only its hash is stored.</param>
    /// <param name="linkBase">
    ///     The scope's own verification link base, or <c>null</c> to use the global setting.
    /// </param>
    Task SendAsync(string email, string token, string? linkBase);
}
