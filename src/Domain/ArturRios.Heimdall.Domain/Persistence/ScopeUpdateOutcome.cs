namespace ArturRios.Heimdall.Domain.Persistence;

/// <summary>
///     The outcome of writing a scope's details (UC-03) — see
///     <see cref="IAtomicWrites.TryUpdateScopeDetailsAsync" />.
/// </summary>
public enum ScopeUpdateOutcome
{
    /// <summary>The details were written.</summary>
    Updated,

    /// <summary>The scope was deleted, or is gone, possibly since the caller read it. Nothing was written.</summary>
    NotFound,

    /// <summary>
    ///     The unique index on the name refused it: another scope took the name between the caller's check and
    ///     the write. Nothing was written.
    /// </summary>
    NameTaken
}
