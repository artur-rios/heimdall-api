namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Tells the holder of an address that someone tried to register it again (UC-47 AF-47b). The
///     concrete delivery is an infrastructure concern, as it is for <see cref="IEmailVerificationSender" />.
/// </summary>
public interface IRegistrationAttemptSender
{
    /// <param name="email">The address that was submitted, as the holder knows it.</param>
    /// <param name="googleAccount">Whether the existing account signs in with Google, which changes
    ///     what the holder is told to do instead.</param>
    Task SendAsync(string email, bool googleAccount);
}
