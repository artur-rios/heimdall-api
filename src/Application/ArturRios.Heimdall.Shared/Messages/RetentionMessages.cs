namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Canonical messages produced by the retention passes (NFR-19). Unlike every other message
///     class here there is no matching <c>MessageMap</c>: these never reach a controller, because
///     the passes are dispatched by a scheduler rather than by a caller, so there is no response for
///     a status code to describe. They exist for the same reason the others do — the audit trail
///     records the application's own messages and never provider text or caller input.
/// </summary>
public static class RetentionMessages
{
    /// <summary>The purge ran to completion. Reported whether or not it removed anything.</summary>
    public const string ExpiredTokensPurged = "Expired single-use tokens purged.";

    /// <summary>The anonymisation pass ran to completion. Reported whether or not it changed anything.</summary>
    public const string ExpiredDeletionsAnonymised = "Logically deleted identities past their retention window anonymised.";

    /// <summary>The audit pseudonymisation pass ran to completion.</summary>
    public const string AuditActorsPseudonymised = "Audit entries past their attribution period pseudonymised.";
}
