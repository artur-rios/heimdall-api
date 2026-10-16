using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.PurgeExpiredTokensCommand" />: how many rows the run removed from
///     each table. Zero everywhere is an ordinary successful run, not a failure — most runs find
///     nothing to do.
/// </summary>
/// <remarks>
///     Deliberately carries no <c>Id</c>: a purge has no single target entity, so the auditing
///     decorator records the entry with a null <c>TargetId</c>, which is the honest answer. The
///     counts are what make the entry useful.
/// </remarks>
public class PurgeExpiredTokensCommandOutput : CommandOutput
{
    /// <summary>Password reset tokens removed (UC-12/UC-13).</summary>
    public int PasswordResetTokensPurged { get; set; }

    /// <summary>Email verification tokens removed (UC-14/UC-15).</summary>
    public int EmailVerificationTokensPurged { get; set; }

    /// <summary>Two-factor email codes removed (UC-36/UC-38).</summary>
    public int TwoFactorEmailCodesPurged { get; set; }

    /// <summary>Unconfirmed pending registrations removed (UC-47).</summary>
    public int PendingRegistrationsPurged { get; set; }

    /// <summary>Revoked or expired application credentials removed (UC-55).</summary>
    public int ApplicationCredentialsPurged { get; set; }

    /// <summary>Registration-attempt notice budgets whose window had ended removed (FR-SR-14).</summary>
    public int AttemptNoticeBudgetsPurged { get; set; }

    /// <summary>Records of used Google ID tokens removed (FR-GO-21).</summary>
    public int GoogleIdTokenUsesPurged { get; set; }

    /// <summary>Rows removed across all seven tables.</summary>
    public int TotalPurged =>
        PasswordResetTokensPurged + EmailVerificationTokensPurged + TwoFactorEmailCodesPurged +
        PendingRegistrationsPurged + ApplicationCredentialsPurged + AttemptNoticeBudgetsPurged +
        GoogleIdTokenUsesPurged;
}
