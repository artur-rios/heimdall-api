using System.Globalization;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     The per-person cooldown on administrator-initiated account email (FR-EV-07, FR-PR-06): at most
///     one email of each kind per person per <see cref="Cooldown" />, whoever asks. Read from
///     <c>HEIMDALL_ADMIN_EMAIL_COOLDOWN_IN_SECONDS</c>; five minutes by default.
/// </summary>
/// <remarks>
///     <para>
///         The cooldown is derived, not stored. A token's issue time is its <c>ExpiresAt</c> minus its
///         kind's lifetime, so "an email of this kind went to this person within the window" is a
///         comparison on token rows that already exist — self-service sends (UC-12, UC-15) included,
///         and whether or not the token has since been used. Changing a token lifetime shifts the
///         derived times of tokens already issued; on a five-minute window that is accepted.
///     </para>
///     <para>
///         Values from 0 to 86400 seconds are accepted, and 0 switches the cooldown off. Anything
///         else — unparsable, negative, larger — falls back to the default rather than to no cooldown
///         at all, because a typo should not remove a protection.
///     </para>
/// </remarks>
public class AdminEmailOptions
{
    public const string CooldownVariable = "HEIMDALL_ADMIN_EMAIL_COOLDOWN_IN_SECONDS";

    private const double DefaultCooldownSeconds = 300;
    private const double MaxCooldownSeconds = 86400;

    /// <summary>The window within which a second email of one kind to one person is refused.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(DefaultCooldownSeconds);

    public static AdminEmailOptions FromEnvironment() => From(Environment.GetEnvironmentVariable(CooldownVariable));

    /// <summary>Reads a configured number of seconds, falling back to the default when it is unusable.</summary>
    public static AdminEmailOptions From(string? configured)
    {
        var seconds =
            double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value is >= 0 and <= MaxCooldownSeconds
                ? value
                : DefaultCooldownSeconds;

        return new AdminEmailOptions { Cooldown = TimeSpan.FromSeconds(seconds) };
    }

    /// <summary>
    ///     For a token kind living <paramref name="tokenLifetime" />: a token of that kind was issued
    ///     inside the cooldown window if and only if its <c>ExpiresAt</c> is later than this.
    /// </summary>
    public DateTime CooldownThreshold(DateTime now, TimeSpan tokenLifetime) => now + tokenLifetime - Cooldown;
}
