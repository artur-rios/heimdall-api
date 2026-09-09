using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to remove the single-use tokens whose retention period has run out (NFR-19): password
///     reset tokens, email verification tokens, and two-factor email codes.
/// </summary>
/// <remarks>
///     <para>
///         Carries no data. The periods and the batch bound are policy, not caller intent — they
///         come from <c>DataRetentionOptions</c>, so one deployment-wide setting governs every run
///         and a caller cannot ask for a shorter retention than the published schedule.
///     </para>
///     <para>
///         Unlike every other command here it reaches no endpoint and has no acting person: it is
///         dispatched by the scheduled purge, and the audit entry it produces is therefore an
///         anonymous write. That entry is the point as much as the deletion is — it is the evidence
///         that the retention schedule was enforced, and when.
///     </para>
/// </remarks>
public class PurgeExpiredTokensCommand : BaseCommand;
