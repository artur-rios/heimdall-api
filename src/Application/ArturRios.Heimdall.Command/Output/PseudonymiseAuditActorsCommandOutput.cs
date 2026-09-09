using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.PseudonymiseAuditActorsCommand" />: how many attributions the run
///     cleared. Zero is an ordinary successful run.
/// </summary>
public class PseudonymiseAuditActorsCommandOutput : CommandOutput
{
    /// <summary>Audit entries whose actor attribution was cleared.</summary>
    public int EntriesPseudonymised { get; set; }
}
