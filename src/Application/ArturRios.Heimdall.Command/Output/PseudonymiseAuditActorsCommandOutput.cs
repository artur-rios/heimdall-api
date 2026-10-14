using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.PseudonymiseAuditActorsCommand" />: how many actor attributions and
///     how many person targets the run cleared. Zero for both is an ordinary successful run.
/// </summary>
/// <remarks>
///     The two counts are separate because they are separate changes: one entry may need its actor
///     cleared, its target cleared, or both, and an entry needing both is counted once in each.
/// </remarks>
public class PseudonymiseAuditActorsCommandOutput : CommandOutput
{
    /// <summary>Audit entries whose actor attribution was cleared.</summary>
    public int EntriesPseudonymised { get; set; }

    /// <summary>Audit entries whose target, a person or Google User, was cleared.</summary>
    public int TargetsCleared { get; set; }
}
