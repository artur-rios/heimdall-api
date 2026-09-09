using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to clear the actor attribution from audit entries whose attribution period has run
///     out, and from entries naming an identity that has since been erased (NFR-21).
/// </summary>
/// <remarks>
///     Carries no data and is dispatched by the scheduled pass, like the other retention commands.
///     Its own audit entry is written with no actor, which is correct: the pass acts for nobody.
/// </remarks>
public class PseudonymiseAuditActorsCommand : BaseCommand;
