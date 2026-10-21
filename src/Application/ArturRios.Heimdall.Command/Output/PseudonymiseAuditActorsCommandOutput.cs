using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.PseudonymiseAuditActorsCommand" />: how many actor attributions and
///     how many person targets the run cleared, and how many tombstones it removed. Zero for all three
///     is an ordinary successful run.
/// </summary>
/// <remarks>
///     The counts are separate because they are separate changes: one entry may need its actor
///     cleared, its target cleared, or both, and an entry needing both is counted once in each. A
///     tombstone is not an audit entry at all, so it has a count of its own.
/// </remarks>
public class PseudonymiseAuditActorsCommandOutput : CommandOutput
{
    /// <summary>Audit entries whose actor attribution was cleared.</summary>
    public int EntriesPseudonymised { get; set; }

    /// <summary>Audit entries whose target, a person or Google User, was cleared.</summary>
    public int TargetsCleared { get; set; }

    /// <summary>
    ///     Tombstones of hard-deleted persons and Google Users removed because no audit entry names
    ///     them any more (see <c>ErasedSubject</c>).
    /// </summary>
    public int TombstonesRemoved { get; set; }

    /// <summary>
    ///     Entries and tombstones still due once the run ended (#206): left because the run reached
    ///     <c>MaxBatchesPerTick</c> full batches. Zero when it drained everything due.
    /// </summary>
    public int Backlog { get; set; }
}
