using System.ComponentModel;

namespace ArturRios.Heimdall.Domain.Enums;

/// <summary>
///     Why an identity was logically deleted. The two answers carry different retention deadlines
///     (Data Retention Schedule §4), because they rest on different legal footing: one is a deadline
///     the law sets, the other a reversal window the controller chooses.
/// </summary>
public enum DeletionKinds
{
    /// <summary>
    ///     An administrator deleted the record (UC-09, UC-28). No data subject asked, so the window
    ///     before anonymisation exists to let an administrator undo a mistake and to give the
    ///     client systems in the scope time to reconcile.
    /// </summary>
    [Description("Deleted by an administrator; anonymised after the reversal window")]
    Administrative = 1,

    /// <summary>
    ///     The data subject asked to be erased. GDPR Art. 12(3) allows at most one month, so the
    ///     deadline is the law's rather than a choice, and it is an outer limit rather than a
    ///     target.
    /// </summary>
    /// <remarks>
    ///     Nothing writes this value yet. The self-service erasure request is UC-42
    ///     (issue #91); this member is the seam it writes through, and the anonymisation pass
    ///     already applies the shorter deadline to anything carrying it.
    /// </remarks>
    [Description("Erasure requested by the data subject; anonymised on the statutory deadline")]
    SubjectRequested = 2
}
