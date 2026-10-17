namespace ArturRios.Heimdall.Domain.Persistence;

/// <summary>
///     The outcome of changing a person's address or role (UC-08, UC-23) — see
///     <see cref="IAtomicWrites.TryChangeEmailAndRoleAsync" />.
/// </summary>
public enum AccountChangeOutcome
{
    /// <summary>The change and the caller's own save were both written.</summary>
    Changed,

    /// <summary>
    ///     The person's address or role was changed, or the person deleted or anonymised, after the
    ///     caller read them. Nothing was written.
    /// </summary>
    Stale,

    /// <summary>
    ///     A unique index refused the new address in the namespace the role chooses (FR-PE-09): another
    ///     identity took it between the caller's check and the write. Nothing was written.
    /// </summary>
    AddressTaken,

    /// <summary>The caller's own save failed, and the change was rolled back with it.</summary>
    SaveFailed
}
