using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     The caller's own two-factor authentication state (FR-2F-15). Carries no secret: never the
///     TOTP secret, never a recovery code, only how many recovery codes remain unspent.
/// </summary>
/// <remarks>
///     A setup initiated by UC-36 but not yet confirmed by UC-37 needs no field of its own — it is
///     exactly <c>!IsActive &amp;&amp; (AppEnabled || EmailEnabled)</c>, since a configuration row
///     only ever exists because setup was initiated. A caller who never initiated setup gets every
///     flag <c>false</c> and <see cref="RemainingRecoveryCodes" /> zero.
/// </remarks>
public class TwoFactorStatusOutput : QueryOutput
{
    /// <summary>Whether two-factor authentication is confirmed and in force (FR-2F-04).</summary>
    public bool IsActive { get; set; }

    /// <summary>Whether the authenticator-app method is configured (FR-2F-02).</summary>
    public bool AppEnabled { get; set; }

    /// <summary>Whether the email method is configured (FR-2F-03).</summary>
    public bool EmailEnabled { get; set; }

    /// <summary>How many issued recovery codes remain unused (FR-2F-05, FR-2F-06).</summary>
    public int RemainingRecoveryCodes { get; set; }
}
