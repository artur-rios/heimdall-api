using System.Globalization;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     The most live pending registrations (UC-47) one scope may hold at a time (#206). Read from
///     <c>HEIMDALL_REGISTRATION_MAX_PENDING_PER_SCOPE</c>; 1000 by default.
/// </summary>
/// <remarks>
///     <para>
///         Each registration a caller sends stores a row and mails a link, and the per-IP rate limit is the
///         only throttle on the caller: enough source addresses outpace the purge, and the table grows
///         without bound. FR-SR-13 bounds what one address can be sent; this bounds the scope, whatever the
///         addresses. A registration beyond it stores and mails nothing and is answered as any other.
///     </para>
///     <para>
///         Any positive integer is accepted. Anything else — unparsable, zero or negative — falls back to the
///         default rather than to no cap, and <see cref="RejectedMaxPendingPerScope" /> says so for start-up to
///         warn, as an unusable retention batch size does.
///     </para>
/// </remarks>
public class RegistrationOptions
{
    /// <summary>Environment variable holding the cap.</summary>
    public const string MaxPendingPerScopeVariable = "HEIMDALL_REGISTRATION_MAX_PENDING_PER_SCOPE";

    /// <summary>The cap when none is configured.</summary>
    public const int DefaultMaxPendingPerScope = 1000;

    /// <summary>The most live pending registrations one scope may hold.</summary>
    public int MaxPendingPerScope { get; init; } = DefaultMaxPendingPerScope;

    /// <summary>The configured text when it was present but unusable; <c>null</c> otherwise.</summary>
    public string? RejectedMaxPendingPerScope { get; init; }

    public static RegistrationOptions FromEnvironment() =>
        From(Environment.GetEnvironmentVariable(MaxPendingPerScopeVariable));

    /// <summary>Reads a configured cap, falling back to the default when it is unusable.</summary>
    public static RegistrationOptions From(string? configured)
    {
        var usable = int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                     && value > 0;

        return new RegistrationOptions
        {
            MaxPendingPerScope = usable ? value : DefaultMaxPendingPerScope,
            RejectedMaxPendingPerScope = usable || string.IsNullOrWhiteSpace(configured) ? null : configured
        };
    }
}
