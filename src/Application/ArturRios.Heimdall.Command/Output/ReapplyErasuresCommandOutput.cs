using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>Result of <see cref="Input.ReapplyErasuresCommand" /> (NFR-25).</summary>
public class ReapplyErasuresCommandOutput : CommandOutput
{
    /// <summary>Identities anonymised by this run.</summary>
    public int Anonymised { get; set; }

    /// <summary>
    ///     Identities that were already anonymised, so the restore did not in fact bring them back.
    /// </summary>
    public int AlreadyAnonymised { get; set; }

    /// <summary>
    ///     Identifiers that matched nothing. Reported rather than treated as an error: a ledger
    ///     naming somebody the restore did not reinstate is the expected case, not a mistake.
    /// </summary>
    public IEnumerable<Guid> NotFound { get; set; } = [];
}
