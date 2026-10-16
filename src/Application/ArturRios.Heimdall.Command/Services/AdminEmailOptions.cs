using System.Globalization;
using ArturRios.Data.Relational.Core.Entities;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     The per-person cooldown on account email (FR-EV-07, FR-PR-06, FR-PR-07, FR-PE-16): at most one
///     email of each kind per person per <see cref="Cooldown" />, whoever asks. Read from
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
///         It began as the administrators' cooldown (UC-51, UC-52) and keeps that variable name, so no
///         deployment has to change its configuration. Anonymous password recovery (UC-12) asks it the
///         same way. A person's own sends — a verification link again, a setup code, an email-change
///         notice — use its <see cref="Cooldown" /> too, but through <see cref="AccountEmailBudget" />,
///         a conditional write on the person row, because a bearer token can send a burst of them.
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

    /// <summary>The cooldown in seconds applied when none, or an unusable one, is configured.</summary>
    public const double DefaultCooldownSeconds = 300;

    private const double MaxCooldownSeconds = 86400;

    /// <summary>The window within which a second email of one kind to one person is refused.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(DefaultCooldownSeconds);

    /// <summary>
    ///     The configured text when it was present but unusable and the default was applied instead;
    ///     <c>null</c> when the variable was absent or usable. Start-up warns on it, so a typo does not
    ///     pass for the value the operator meant.
    /// </summary>
    public string? RejectedCooldown { get; init; }

    /// <summary>These options with the cooldown at most <paramref name="limit" />.</summary>
    public AdminEmailOptions CappedAt(TimeSpan limit) =>
        Cooldown <= limit ? this : new AdminEmailOptions { Cooldown = limit, RejectedCooldown = RejectedCooldown };

    public static AdminEmailOptions FromEnvironment() => From(Environment.GetEnvironmentVariable(CooldownVariable));

    /// <summary>Reads a configured number of seconds, falling back to the default when it is unusable.</summary>
    public static AdminEmailOptions From(string? configured)
    {
        var usable =
            double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value is >= 0 and <= MaxCooldownSeconds;

        return new AdminEmailOptions
        {
            Cooldown = TimeSpan.FromSeconds(usable ? value : DefaultCooldownSeconds),
            RejectedCooldown = usable || string.IsNullOrWhiteSpace(configured) ? null : configured
        };
    }

    /// <summary>
    ///     For a token kind living <paramref name="tokenLifetime" />: a token of that kind was issued
    ///     inside the cooldown window if and only if its <c>ExpiresAt</c> is later than this.
    /// </summary>
    public DateTime CooldownThreshold(DateTime now, TimeSpan tokenLifetime) => now + tokenLifetime - Cooldown;

    /// <summary>
    ///     Whether any token of this kind (used, superseded or live, self-service or not) was issued for
    ///     the person with internal id <paramref name="personId" /> inside the cooldown window, so that
    ///     UC-51 and UC-52 ask the question the same way (AF-51e, AF-52e).
    /// </summary>
    public async Task<bool> IsRunningAsync<TToken>(
        IAsyncReadOnlyRepository<TToken, long> tokenReader,
        long personId,
        TimeSpan tokenLifetime,
        DateTime now,
        CancellationToken cancellationToken = default)
        where TToken : Entity<long>, IPersonToken
    {
        var threshold = CooldownThreshold(now, tokenLifetime);

        return await tokenReader.Query()
            .AnyAsync(x => x.PersonId == personId && x.ExpiresAt > threshold, cancellationToken);
    }
}
