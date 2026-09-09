using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Stamps the lawful basis and the privacy notice version onto an identity as it is created
///     (NFR-23).
/// </summary>
/// <remarks>
///     <para>
///         GDPR Art. 5(2) and LGPD Art. 8 §2 put the burden of demonstrating the basis on the
///         controller. Demonstrating it means being able to say, of a particular person, what the
///         basis was and what they were told — which a system that derives the answer at read time
///         cannot do, because it answers what the basis is *now*.
///     </para>
///     <para>
///         The basis comes from the scope where the scope declares one, because the scope is the
///         tenant boundary and the tenant is usually the controller for the identities inside it.
///         Where it does not, contract performance applies: the identity exists to run a service the
///         person is party to, which is what every other row of the record already says.
///     </para>
/// </remarks>
public static class LegalBasisRecorder
{
    /// <summary>
    ///     The version of the privacy notice this build ships. Bumped whenever the notice changes
    ///     materially, so an identity records the version actually in force when it was created.
    /// </summary>
    public const string CurrentPrivacyNoticeVersion = "1.0";

    /// <summary>The basis applied when a scope declares none, and for identities outside any scope.</summary>
    public const LegalBases DefaultBasis = LegalBases.ContractPerformance;

    /// <summary>Records the basis and notice version on a person being created.</summary>
    public static void Record(Person person, Scope? scope, DateTime at)
    {
        person.LegalBasis = (int)Resolve(scope);
        person.PrivacyNoticeVersion = CurrentPrivacyNoticeVersion;
        person.BasisRecordedAt = at;
    }

    /// <summary>Records the basis and notice version on a Google User being created.</summary>
    public static void Record(GoogleUser googleUser, Scope? scope, DateTime at)
    {
        googleUser.LegalBasis = (int)Resolve(scope);
        googleUser.PrivacyNoticeVersion = CurrentPrivacyNoticeVersion;
        googleUser.BasisRecordedAt = at;
    }

    /// <summary>
    ///     The basis a scope declares, or the default. <see cref="LegalBases.Consent" /> is never
    ///     returned even if somehow stored: it needs a withdrawal path that does not exist, and
    ///     honouring a stored value here would let a deployment record a basis it could not honour.
    ///     The validators refuse it on the way in; this refuses it on the way out too, because a
    ///     guard that exists in one direction only is one migration away from being bypassed.
    /// </summary>
    public static LegalBases Resolve(Scope? scope)
    {
        if (scope?.DefaultLegalBasis is not { } declared)
        {
            return DefaultBasis;
        }

        var basis = (LegalBases)declared;

        return basis is LegalBases.Consent or LegalBases.Unrecorded || !Enum.IsDefined(basis)
            ? DefaultBasis
            : basis;
    }
}
