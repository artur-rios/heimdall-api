using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>
///     Handles <see cref="DetectSecuritySignalsQuery" /> (NFR-26): reads the signals the system
///     already produces and reports the ones worth telling somebody about.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> The API produced strong signals and nothing consumed any of them.
///         The audit trail records every refused write with its reason (NFR-09), the lockout
///         counters track per-account brute force, and the hash gate sheds under load — all of it
///         written down, none of it read. A trail nobody reads does not make anyone <em>aware</em>
///         of anything, and awareness is what starts GDPR Art. 33's seventy-two hours and ANPD
///         Resolution 15/2024's three working days.
///     </para>
///     <para>
///         <b>Signals are rates, not totals.</b> Every threshold is measured inside a window,
///         because twenty refusals over a year is somebody who forgets their password and twenty in
///         a quarter of an hour is somebody trying things. A total would alert on the former and
///         never notice the latter.
///     </para>
///     <para>
///         <b>What this deliberately is not.</b> It does not decide that a breach has occurred, and
///         it cannot: a signal is a reason to look, and the runbook is what turns looking into a
///         declaration. Automating the declaration would put the seventy-two-hour clock in the hands
///         of a threshold.
///     </para>
/// </remarks>
public class DetectSecuritySignalsQueryHandler(
    IAsyncReadOnlyRepository<AuditLog> auditReader,
    IAsyncReadOnlyRepository<Person> personReader,
    DataRetentionOptions options)
    : IQueryHandlerAsync<DetectSecuritySignalsQuery, SecuritySignalsOutput>
{
    /// <summary>Sustained refusals against one actor — somebody trying things they may not do.</summary>
    public const string RepeatedRefusals = "REPEATED_REFUSALS";

    /// <summary>More accounts locked out at once than ordinary forgetfulness explains.</summary>
    public const string LockoutSpike = "LOCKOUT_SPIKE";

    /// <summary>
    ///     The hash gate shedding — the process at its concurrent-derivation limit, which Threat
    ///     Model TH-03 describes as a load condition and an attacker would recognise as a lever.
    /// </summary>
    public const string CredentialVerificationShedding = "CREDENTIAL_VERIFICATION_SHEDDING";

    public async Task<DataOutput<SecuritySignalsOutput?>> HandleAsync(DetectSecuritySignalsQuery query)
    {
        var output = DataOutput<SecuritySignalsOutput?>.New;
        var now = DateTime.UtcNow;
        var since = now - options.MonitoringWindow;

        var signals = new List<SecuritySignal>();

        // Refusals grouped by actor. Anonymous writes are excluded: they have no actor to group by,
        // and the per-IP limiter is what bounds them.
        var refusalsByActor = await auditReader.Query()
            .Where(entry => entry.CreatedAt >= since && !entry.Succeeded && entry.ActorPersonId != null)
            .GroupBy(entry => entry.ActorPersonId)
            .Select(group => new { ActorId = group.Key, Count = group.Count() })
            .Where(group => group.Count >= options.RefusalThreshold)
            .ToListAsync();

        signals.AddRange(refusalsByActor.Select(group => new SecuritySignal
        {
            Kind = RepeatedRefusals,
            Description =
                $"One identity had {group.Count} write refused in the last {options.MonitoringWindow}. "
                + "Sustained refusals are what somebody probing for what they may do looks like.",
            Count = group.Count,
            ActorId = group.ActorId
        }));

        // Lockouts are counted as a population rather than per account: one locked-out person is
        // ordinary, and the thing worth knowing is many at once, which is what credential stuffing
        // against a list of addresses produces.
        var lockedOut = await personReader.Query()
            .CountAsync(person => person.LockedOutUntil != null && person.LockedOutUntil > now);

        if (lockedOut >= options.LockoutThreshold)
        {
            signals.Add(new SecuritySignal
            {
                Kind = LockoutSpike,
                Description =
                    $"{lockedOut} accounts are locked out at once. One is ordinary; many together is "
                    + "what credential stuffing against a list of addresses looks like.",
                Count = lockedOut
            });
        }

        // The gate's own refusals reach the trail as this canonical message, so the shedding TH-03
        // describes is detectable without instrumenting the gate itself.
        var shedding = await auditReader.Query()
            .CountAsync(entry => entry.CreatedAt >= since
                                 && entry.FailureReason == AuthMessages.AuthenticationTemporarilyUnavailable);

        if (shedding >= options.RefusalThreshold)
        {
            signals.Add(new SecuritySignal
            {
                Kind = CredentialVerificationShedding,
                Description =
                    $"Credential verification was shed {shedding} times in the last "
                    + $"{options.MonitoringWindow}. The process is at its concurrent-derivation limit "
                    + "(Threat Model TH-03), which is a load condition and also a lever.",
                Count = shedding
            });
        }

        return output.WithData(new SecuritySignalsOutput
        {
            DetectedAt = now,
            Window = options.MonitoringWindow,
            Signals = signals
        });
    }
}
