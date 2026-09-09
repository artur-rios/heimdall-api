using System.ComponentModel;

namespace ArturRios.Heimdall.Domain.Enums;

/// <summary>
///     Why processing of an identity is restricted. These are the four grounds GDPR Art. 18(1)
///     lists, which LGPD Art. 18 III–IV covers as *bloqueio*.
/// </summary>
/// <remarks>
///     An enum rather than free text, deliberately. Art. 18(1) is exhaustive — a restriction rests
///     on one of these four or it is not a restriction under the article — so a text field would
///     buy nothing the ground does not already say, while storing unbounded prose written by the
///     subject: more personal data, with no purpose of its own and no retention story.
/// </remarks>
public enum RestrictionGrounds
{
    /// <summary>
    ///     The subject contests the accuracy of the data, and processing is restricted while it is
    ///     verified — Art. 18(1)(a).
    /// </summary>
    [Description("The subject contests the accuracy of the data")]
    AccuracyContested = 1,

    /// <summary>
    ///     The processing is unlawful and the subject opposes erasure, asking for restriction
    ///     instead — Art. 18(1)(b).
    /// </summary>
    [Description("Processing is unlawful and the subject prefers restriction to erasure")]
    UnlawfulButErasureOpposed = 2,

    /// <summary>
    ///     The controller no longer needs the data, but the subject needs it kept for a legal claim
    ///     — Art. 18(1)(c).
    /// </summary>
    [Description("No longer needed by the controller, but required by the subject for a legal claim")]
    NeededForLegalClaim = 3,

    /// <summary>
    ///     The subject has objected under Art. 21, and processing is restricted while the
    ///     controller's grounds are weighed against theirs — Art. 18(1)(d).
    /// </summary>
    [Description("The subject has objected, pending verification of the controller's grounds")]
    ObjectionPending = 4
}
