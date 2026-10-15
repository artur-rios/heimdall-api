using System.Globalization;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     A person-token lifetime read from <c>HEIMDALL_AUTH_TOKEN_EXPIRATION_IN_SECONDS</c>: the seconds
///     to use, and the configured text when it was present but unusable and the default applied in its
///     place. Pulled out of <c>Startup.BuildJwtConfiguration</c> so the parse can be tested and the
///     fallback reported.
/// </summary>
/// <param name="Seconds">The lifetime to use.</param>
/// <param name="RejectedValue">
///     The configured text when it was present but unusable; <c>null</c> when the variable was absent
///     or usable (absent is the documented default, not a fallback).
/// </param>
public sealed record TokenLifetimeSetting(double Seconds, string? RejectedValue)
{
    /// <summary>
    ///     Reads <paramref name="configured" /> as a positive, finite number of seconds, using
    ///     <paramref name="defaultSeconds" /> when it is absent or unusable.
    /// </summary>
    public static TokenLifetimeSetting Parse(string? configured, double defaultSeconds)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return new TokenLifetimeSetting(defaultSeconds, null);
        }

        return double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
               && double.IsFinite(seconds) && seconds > 0
            ? new TokenLifetimeSetting(seconds, null)
            : new TokenLifetimeSetting(defaultSeconds, configured);
    }
}
