namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Tells the caller of the current request how long to wait before trying again — the
///     <c>Retry-After</c> header of a <c>429</c> — when a handler refuses for a reason that ends at a
///     known time, such as a spent account-email budget (FR-PE-16).
/// </summary>
/// <remarks>
///     A handler answers with a <c>DataOutput</c>, which carries no headers, and the controller
///     cannot tell how long the wait is without reading what the handler read. This seam lets the
///     handler say it, and the host decide how to deliver it.
/// </remarks>
public interface IRetryAfterAdvisor
{
    /// <summary>Advises the caller to wait <paramref name="wait" /> before retrying.</summary>
    void Advise(TimeSpan wait);
}
