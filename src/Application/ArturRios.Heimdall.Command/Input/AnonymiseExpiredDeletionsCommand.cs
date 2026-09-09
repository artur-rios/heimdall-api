using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to anonymise the logically deleted identities whose retention window has run out
///     (NFR-20): persons and Google Users past the deadline their <c>DeletionKind</c> carries.
/// </summary>
/// <remarks>
///     Carries no data, and is dispatched by the scheduled pass rather than by a caller, for the
///     same reasons as <see cref="PurgeExpiredTokensCommand" />. The audit entry it produces is an
///     anonymous write, and it is the evidence that erasure happened — which matters more here than
///     for the token purge, because this is the operation a data subject's right under GDPR Art. 17
///     and LGPD Art. 16 is actually discharged by.
/// </remarks>
public class AnonymiseExpiredDeletionsCommand : BaseCommand;
