using System.Globalization;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     How long a scope owner invitation stays acceptable (UC-21, FR-SC-18). Read from
///     <c>HEIMDALL_SCOPE_INVITATION_LIFETIME_IN_SECONDS</c>; seven days by default.
/// </summary>
/// <remarks>
///     Values from 3600 (an hour) to 2592000 (thirty days) are accepted. Anything else — unparsable or out
///     of range — falls back to the default rather than to an invitation that never expires, and
///     <see cref="RejectedLifetime" /> says so for start-up to warn.
/// </remarks>
public class ScopeInvitationOptions
{
    public const string LifetimeVariable = "HEIMDALL_SCOPE_INVITATION_LIFETIME_IN_SECONDS";

    /// <summary>Seven days, in seconds.</summary>
    public const double DefaultLifetimeSeconds = 604800;

    private const double MinLifetimeSeconds = 3600;

    private const double MaxLifetimeSeconds = 2592000;

    /// <summary>How long after its creation an invitation can still be accepted.</summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromSeconds(DefaultLifetimeSeconds);

    /// <summary>The configured text when it was present but unusable; <c>null</c> otherwise.</summary>
    public string? RejectedLifetime { get; init; }

    public static ScopeInvitationOptions FromEnvironment() => From(Environment.GetEnvironmentVariable(LifetimeVariable));

    /// <summary>Reads a configured number of seconds, falling back to the default when it is unusable.</summary>
    public static ScopeInvitationOptions From(string? configured)
    {
        var usable =
            double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value is >= MinLifetimeSeconds and <= MaxLifetimeSeconds;

        return new ScopeInvitationOptions
        {
            Lifetime = TimeSpan.FromSeconds(usable ? value : DefaultLifetimeSeconds),
            RejectedLifetime = usable || string.IsNullOrWhiteSpace(configured) ? null : configured
        };
    }
}
