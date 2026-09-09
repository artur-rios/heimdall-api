namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Tells a data subject that the restriction on processing their data is about to be lifted
///     (GDPR Art. 18(3)).
/// </summary>
/// <remarks>
///     <para>
///         Separate from the other senders, and reporting success rather than swallowing failure,
///         because this delivery is <em>load-bearing</em>. Everywhere else in this API a delivery
///         failure is deliberately not the caller's problem: a verification email that does not
///         arrive is re-requested, and surfacing the failure would leak whether an address exists.
///     </para>
///     <para>
///         Art. 18(3) makes this one different. The subject "shall be informed before the
///         restriction is lifted" — informing is a precondition of the act, not a courtesy that
///         follows it. A lift that proceeded after a failed send would be unlawful and would look
///         identical to one that worked, so the result has to be observable.
///     </para>
/// </remarks>
public interface IRestrictionLiftNotifier
{
    /// <summary>
    ///     Attempts to inform <paramref name="email" />. Returns whether the notification was
    ///     accepted for delivery.
    /// </summary>
    Task<bool> NotifyAsync(string email);
}
