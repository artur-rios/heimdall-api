using System.ComponentModel;

namespace ArturRios.Heimdall.Domain.Enums;

/// <summary>
///     The lawful basis on which an identity's personal data is processed, recorded per identity so
///     the controller can demonstrate it (GDPR Art. 5(2) and Art. 30(1)(c), LGPD Art. 8 §2 and Art.
///     37). The mapping between the two laws' articles is in §5 of the Data Protection Document.
/// </summary>
public enum LegalBases
{
    /// <summary>
    ///     The basis was not recorded, because the identity predates the mechanism that records it.
    /// </summary>
    /// <remarks>
    ///     An explicit member rather than a null or a guess. The migration that added these columns
    ///     could have written the basis that almost certainly applied, and that would have produced
    ///     a record asserting something nobody checked — the opposite of what accountability means.
    ///     A row reading "unrecorded" is honest and is visibly a thing to fix.
    /// </remarks>
    [Description("Predates the recording mechanism; the basis was never captured")]
    Unrecorded = 0,

    /// <summary>
    ///     Necessary to perform the service the person is party to — GDPR Art. 6(1)(b), LGPD Art. 7
    ///     V. The basis for almost everything here: an identity provider cannot authenticate an
    ///     identity it does not hold.
    /// </summary>
    [Description("Necessary to perform the service the person is party to")]
    ContractPerformance = 1,

    /// <summary>
    ///     Necessary for interests the controller or a third party pursues — GDPR Art. 6(1)(f),
    ///     LGPD Art. 7 IX. Covers the security data: defending accounts against people trying to
    ///     break into them.
    /// </summary>
    [Description("Necessary for the controller's legitimate interests, chiefly account security")]
    LegitimateInterests = 2,

    /// <summary>
    ///     Required of the controller by law — GDPR Art. 6(1)(c), LGPD Art. 7 II. Covers the erasure
    ///     records, which exist because the law requires the controller to honour and evidence a
    ///     request.
    /// </summary>
    [Description("Required of the controller by law, such as the erasure records")]
    LegalObligation = 3,

    /// <summary>
    ///     The person agreed — GDPR Art. 6(1)(a), LGPD Art. 7 I.
    /// </summary>
    /// <remarks>
    ///     Present for completeness of the record and <b>not selectable</b>. Consent is withdrawable
    ///     at any moment (GDPR Art. 7(3)), and withdrawal has to be as easy as giving it — which
    ///     needs a withdrawal path, and needs the processing to stop when it is used. Neither
    ///     exists, and an identity service that stopped verifying passwords on withdrawal would be
    ///     broken rather than compliant. Offering this value while neither holds would let a
    ///     deployment record a basis it could not honour, so the validators refuse it.
    /// </remarks>
    [Description("The person agreed. Not selectable: no withdrawal path exists")]
    Consent = 4
}
