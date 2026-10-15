using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>The outcome of <see cref="PasswordRecoveryEligibility.Of" />.</summary>
public enum RecoveryEligibility
{
    /// <summary>A reset link would let this person sign in again.</summary>
    Eligible,

    /// <summary>Processing of the person is restricted (NFR-24); sending would be processing them.</summary>
    Restricted,

    /// <summary>The person is logically deleted, or their record has been anonymised (NFR-20).</summary>
    Deleted,

    /// <summary>
    ///     The account could not sign in whatever its password: a <c>User</c> whose scope is logically
    ///     deleted or missing (FR-AU-06), or a <c>ScopeAdmin</c> with no live scope (FR-AU-07).
    /// </summary>
    CannotSignIn
}

/// <summary>
///     Who a password-reset link is worth sending to: UC-12's rule, shared with UC-52 so the anonymous
///     recovery and the administrator's request cannot disagree (FR-PR-02, FR-PR-05). Run again when
///     a reset or verification link is followed (UC-13, UC-14: FR-PR-08, FR-EV-08), so a link issued
///     before the account changed cannot act on it afterwards.
/// </summary>
/// <remarks>
///     It mirrors the account checks UC-11 applies at login: a link is only worth issuing to someone
///     who could use the resulting password. UC-12 needs only yes or no, since it answers every caller
///     alike; UC-52's caller is authenticated and owed the reason, so the outcome names it. The
///     restriction is checked first, as UC-12 always has, so a person both restricted and deleted is
///     reported as restricted. An anonymised record counts as deleted whatever its flag says: it
///     belongs to nobody, and its address is a placeholder.
/// </remarks>
public static class PasswordRecoveryEligibility
{
    /// <summary>
    ///     Evaluates <paramref name="person" />. <c>ScopeMembership.Scope</c> and every
    ///     <c>ScopeOwnerships[].Scope</c> must be loaded.
    /// </summary>
    public static RecoveryEligibility Of(Person person)
    {
        if (person.ProcessingRestrictedAt is not null)
        {
            return RecoveryEligibility.Restricted;
        }

        if (person.IsDeleted || person.AnonymisedAt is not null)
        {
            return RecoveryEligibility.Deleted;
        }

        var maySignIn = person.RoleId switch
        {
            (long)Roles.User => person.ScopeMembership is { Scope.IsDeleted: false },
            (long)Roles.ScopeAdmin => person.ScopeOwnerships.Any(ownership => !ownership.Scope.IsDeleted),
            _ => true
        };

        return maySignIn ? RecoveryEligibility.Eligible : RecoveryEligibility.CannotSignIn;
    }
}
