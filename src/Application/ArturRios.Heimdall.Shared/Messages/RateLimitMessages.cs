namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Canonical messages for requests the rate limiter turns away (TH-32, NFR). Held beside the
///     other shared messages rather than inline in <c>Startup</c> so the wording is one constant that
///     tests and documentation can name.
/// </summary>
public static class RateLimitMessages
{
    /// <summary>
    ///     The body of a <c>429</c> from the <c>AuthAnonymous</c> policy. It says only that the caller
    ///     should wait: which bucket was spent, and how, is not something to hand an attacker.
    /// </summary>
    public const string TooManyRequests = "Too many requests. Try again later.";
}
