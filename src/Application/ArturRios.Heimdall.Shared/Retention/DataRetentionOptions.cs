namespace ArturRios.Heimdall.Shared.Retention;

/// <summary>
///     The retention periods NFR-19 requires the API to enforce, read from the
///     <c>HEIMDALL_RETENTION_*</c> environment variables. The full schedule — every table holding
///     personal data, its period, and the reason for it — is the Data Retention Schedule Document;
///     this type carries only the periods the API enforces itself, so nothing here promises a
///     retention rule that no code applies.
/// </summary>
/// <remarks>
///     <para>
///         Every value has a default that is safe to run with, because a deployment that sets none
///         of these variables must still enforce a period: an unset variable meaning "keep forever"
///         is exactly the state NFR-19 exists to end.
///     </para>
///     <para>
///         A malformed or out-of-range value falls back to the default rather than failing start-up.
///         The alternative was considered and rejected: refusing to start would turn a typo in an
///         operator's retention setting into an outage of an identity provider that every client
///         system authenticates against, which is a far worse failure than running one interval on
///         the documented default. The fallback is logged by the caller.
///     </para>
/// </remarks>
public sealed record DataRetentionOptions
{
    /// <summary>Environment variable holding the single-use token grace period, in days.</summary>
    public const string SingleUseTokenGraceDaysVariable = "HEIMDALL_RETENTION_TOKEN_GRACE_DAYS";

    /// <summary>Environment variable holding the interval between purge runs, in minutes.</summary>
    public const string PurgeIntervalMinutesVariable = "HEIMDALL_RETENTION_PURGE_INTERVAL_MINUTES";

    /// <summary>Environment variable holding the maximum rows removed from one table per run.</summary>
    public const string PurgeBatchSizeVariable = "HEIMDALL_RETENTION_PURGE_BATCH_SIZE";

    /// <summary>Environment variable switching the scheduled purge off (<c>false</c> disables it).</summary>
    public const string PurgeEnabledVariable = "HEIMDALL_RETENTION_PURGE_ENABLED";

    /// <summary>Environment variable holding the statutory erasure deadline, in days.</summary>
    public const string SubjectErasureDeadlineDaysVariable = "HEIMDALL_RETENTION_ERASURE_DEADLINE_DAYS";

    /// <summary>Environment variable holding the administrative reversal window, in days.</summary>
    public const string AdministrativeDeletionWindowDaysVariable = "HEIMDALL_RETENTION_DELETION_WINDOW_DAYS";

    /// <summary>Environment variable switching the scheduled anonymisation off.</summary>
    public const string AnonymisationEnabledVariable = "HEIMDALL_RETENTION_ANONYMISATION_ENABLED";

    /// <summary>Environment variable holding how long an audit entry stays attributed, in days.</summary>
    public const string AuditActorRetentionDaysVariable = "HEIMDALL_RETENTION_AUDIT_ACTOR_DAYS";

    /// <summary>Environment variable switching the scheduled audit pseudonymisation off.</summary>
    public const string AuditPseudonymisationEnabledVariable = "HEIMDALL_RETENTION_AUDIT_PSEUDONYMISATION_ENABLED";

    /// <summary>Environment variable holding how long application log files are kept, in days.</summary>
    public const string LogRetentionDaysVariable = "HEIMDALL_RETENTION_LOG_DAYS";

    /// <summary>Environment variable switching security signal detection off.</summary>
    public const string SecurityMonitoringEnabledVariable = "HEIMDALL_MONITORING_ENABLED";

    /// <summary>Environment variable holding the window each signal is measured over, in minutes.</summary>
    public const string MonitoringWindowMinutesVariable = "HEIMDALL_MONITORING_WINDOW_MINUTES";

    /// <summary>Environment variable holding how many refusals by one actor raise a signal.</summary>
    public const string RefusalThresholdVariable = "HEIMDALL_MONITORING_REFUSAL_THRESHOLD";

    /// <summary>Environment variable holding how many locked-out accounts raise a signal.</summary>
    public const string LockoutThresholdVariable = "HEIMDALL_MONITORING_LOCKOUT_THRESHOLD";

    /// <summary>Seven days — see <see cref="SingleUseTokenGrace" /> for why it is not zero.</summary>
    public static readonly TimeSpan DefaultSingleUseTokenGrace = TimeSpan.FromDays(7);

    /// <summary>One hour. Nothing here is urgent; the grace period dwarfs the interval.</summary>
    public static readonly TimeSpan DefaultPurgeInterval = TimeSpan.FromHours(1);

    /// <summary>Rows removed from one table in one run, by default.</summary>
    public const int DefaultPurgeBatchSize = 500;

    /// <summary>Thirty days — the outer limit GDPR Art. 12(3) allows, not a chosen period.</summary>
    public static readonly TimeSpan DefaultSubjectErasureDeadline = TimeSpan.FromDays(30);

    /// <summary>Ninety days — the reversal window for an administrative deletion.</summary>
    public static readonly TimeSpan DefaultAdministrativeDeletionWindow = TimeSpan.FromDays(90);

    /// <summary>548 days — eighteen months, the period the retention schedule sets.</summary>
    public static readonly TimeSpan DefaultAuditActorRetention = TimeSpan.FromDays(548);

    /// <summary>365 days — twelve months, the period the retention schedule sets for logs.</summary>
    public static readonly TimeSpan DefaultLogRetention = TimeSpan.FromDays(365);

    /// <summary>Fifteen minutes — long enough to see a pattern, short enough to still be news.</summary>
    public static readonly TimeSpan DefaultMonitoringWindow = TimeSpan.FromMinutes(15);

    /// <summary>Refusals by one actor in the window before it is worth telling somebody.</summary>
    public const int DefaultRefusalThreshold = 20;

    /// <summary>Accounts locked out at once before it is worth telling somebody.</summary>
    public const int DefaultLockoutThreshold = 10;

    /// <summary>Bounds on the monitoring window, in minutes.</summary>
    private const double MinimumMonitoringWindowMinutes = 1;

    /// <inheritdoc cref="MinimumMonitoringWindowMinutes" />
    private const double MaximumMonitoringWindowMinutes = 1440;

    /// <summary>Shortest accepted log retention, in days.</summary>
    private const double MinimumLogRetentionDays = 1;

    /// <summary>Longest accepted log retention, in days.</summary>
    private const double MaximumLogRetentionDays = 3650;

    /// <summary>
    ///     Shortest accepted audit attribution retention, in days. Matches the floor the database
    ///     trigger enforces: below it the pass would select rows the trigger then refuses, turning a
    ///     configuration mistake into a run that fails every time rather than one that quietly does
    ///     the wrong thing.
    /// </summary>
    private const double MinimumAuditActorRetentionDays = 30;

    /// <summary>Longest accepted audit attribution retention, in days.</summary>
    private const double MaximumAuditActorRetentionDays = 3650;

    /// <summary>
    ///     Longest accepted erasure deadline, in days. Thirty is the statutory limit, so a value
    ///     above it is not a policy choice but a compliance failure, and the ceiling refuses it.
    /// </summary>
    private const double MaximumErasureDeadlineDays = 30;

    /// <summary>
    ///     Longest accepted administrative window, in days. Two years is already far past any
    ///     reversal purpose; beyond it the window has stopped being a window.
    /// </summary>
    private const double MaximumDeletionWindowDays = 730;

    /// <summary>
    ///     Longest accepted grace period, in days. Ten years is far past any purpose these tokens
    ///     could serve, so a larger value is a typo — a stray unit, usually — rather than a policy,
    ///     and honouring it would keep the data indefinitely under the appearance of a stated period.
    /// </summary>
    private const double MaximumGraceDays = 3650;

    /// <summary>
    ///     Shortest accepted purge interval, in minutes. Keeps a mistyped value from turning the
    ///     purge into continuous delete pressure on the tables the login path writes to.
    /// </summary>
    private const double MinimumIntervalMinutes = 1;

    /// <summary>
    ///     Longest accepted purge interval, in minutes — seven days.
    /// </summary>
    /// <remarks>
    ///     A ceiling is needed, not just tidy: <see cref="PeriodicTimer" /> refuses a period beyond
    ///     roughly 49 days, and an interval it rejects would throw inside the hosted service, whose
    ///     default behaviour on an unhandled exception is to stop the host. Without this, a stray
    ///     digit in a retention setting would take the API down.
    /// </remarks>
    private const double MaximumIntervalMinutes = 7 * 24 * 60;

    /// <summary>
    ///     How long a single-use token is kept after it expires, before the purge removes it
    ///     (GDPR Art. 5(1)(e), LGPD Art. 15 III).
    /// </summary>
    /// <remarks>
    ///     Deliberately not zero. UC-13 answers a presented token with three distinct outcomes —
    ///     <c>TokenInvalid</c>, <c>TokenExpired</c>, <c>TokenAlreadyUsed</c> — and purging a row the
    ///     moment it expires collapses the last two into the first, so a person following a stale
    ///     link is told the token never existed rather than that it ran out. The grace period is
    ///     what keeps that answer truthful for as long as anyone is plausibly still holding the
    ///     link, and no longer.
    /// </remarks>
    public TimeSpan SingleUseTokenGrace { get; init; } = DefaultSingleUseTokenGrace;

    /// <summary>How often the scheduled purge runs.</summary>
    public TimeSpan PurgeInterval { get; init; } = DefaultPurgeInterval;

    /// <summary>
    ///     The most rows the purge removes from any one table in a single run.
    /// </summary>
    /// <remarks>
    ///     The bound is not a performance nicety. SRD §6.3.2 measured the write path degrading with
    ///     the size of the table under sustained insert pressure, and a purge is sustained delete
    ///     pressure on tables the login and recovery paths write to. Bounding the batch means a
    ///     backlog drains over several runs instead of one long transaction competing with live
    ///     traffic.
    /// </remarks>
    public int PurgeBatchSize { get; init; } = DefaultPurgeBatchSize;

    /// <summary>
    ///     Whether the scheduled purge is registered at start-up. On by default: a deployment that
    ///     sets nothing still enforces the schedule.
    /// </summary>
    public bool PurgeEnabled { get; init; } = true;

    /// <summary>
    ///     How long a logically deleted identity is kept before anonymisation when the data subject
    ///     asked to be erased (NFR-20).
    /// </summary>
    /// <remarks>
    ///     An outer limit rather than a target. GDPR Art. 12(3) allows at most one month to act on
    ///     an erasure request, so this is the deadline the erasure must not exceed — it completes as
    ///     soon as it can. Raising it above thirty days is refused, because no configuration can
    ///     make a longer period lawful.
    /// </remarks>
    public TimeSpan SubjectErasureDeadline { get; init; } = DefaultSubjectErasureDeadline;

    /// <summary>
    ///     How long a logically deleted identity is kept before anonymisation when an administrator
    ///     deleted it and no subject asked (NFR-20).
    /// </summary>
    /// <remarks>
    ///     A reversal window: an administrator who deleted the wrong person needs to undo it, and
    ///     the client systems in that person's scope need time to notice. Ninety days rather than
    ///     the thirty most consumer services use, because a deletion here is not confined to one
    ///     product — every client system authenticates against this one.
    /// </remarks>
    public TimeSpan AdministrativeDeletionWindow { get; init; } = DefaultAdministrativeDeletionWindow;

    /// <summary>
    ///     Whether the scheduled anonymisation is registered at start-up. On by default, for the
    ///     same reason the purge is.
    /// </summary>
    public bool AnonymisationEnabled { get; init; } = true;

    /// <summary>
    ///     How long an audit entry stays attributed to the identity that produced it, before the
    ///     attribution is cleared (NFR-21).
    /// </summary>
    /// <remarks>
    ///     Eighteen months by default. Most security baselines settle on twelve — PCI DSS requires a
    ///     year of audit history — and breach discovery lag routinely exceeds it, so twelve is a
    ///     floor rather than a comfortable answer; eighteen covers a compliance cycle plus that lag.
    ///     Past it the accountability value of <em>who</em> falls away sharply while the value of
    ///     what happened, when and how often does not, and that half survives the attribution being
    ///     cleared.
    /// </remarks>
    public TimeSpan AuditActorRetention { get; init; } = DefaultAuditActorRetention;

    /// <summary>Whether the scheduled audit pseudonymisation is registered at start-up.</summary>
    public bool AuditPseudonymisationEnabled { get; init; } = true;

    /// <summary>
    ///     How long application log files are kept before the file sink removes them (NFR-22).
    /// </summary>
    /// <remarks>
    ///     Twelve months, because these are the telemetry a breach is detected from rather than
    ///     debugging output: breach discovery is measured in months, and a shorter period would
    ///     delete the evidence before anyone knew to look. GDPR Art. 33's clock starts at awareness,
    ///     which a log that had already expired never contributed to.
    /// </remarks>
    public TimeSpan LogRetention { get; init; } = DefaultLogRetention;

    /// <summary>Whether security signal detection is registered at start-up (NFR-26).</summary>
    public bool SecurityMonitoringEnabled { get; init; } = true;

    /// <summary>
    ///     How far back each signal looks. A signal is about a rate, not a total: twenty refusals
    ///     over a year is somebody who forgets their password, and twenty in a quarter of an hour is
    ///     somebody trying things.
    /// </summary>
    public TimeSpan MonitoringWindow { get; init; } = DefaultMonitoringWindow;

    /// <summary>Refusals by one actor within the window before a signal is raised.</summary>
    public int RefusalThreshold { get; init; } = DefaultRefusalThreshold;

    /// <summary>Accounts locked out at once before a signal is raised.</summary>
    public int LockoutThreshold { get; init; } = DefaultLockoutThreshold;

    /// <summary>
    ///     Names the variables whose values were unusable and fell back to a default, so the caller
    ///     can say so in the log. Empty when every variable was absent or valid — an absent variable
    ///     is not a fallback, it is the documented default being chosen.
    /// </summary>
    public IReadOnlyCollection<string> InvalidVariables { get; init; } = [];

    /// <summary>Reads the retention settings from the current process environment.</summary>
    public static DataRetentionOptions FromEnvironment()
    {
        var invalid = new List<string>();

        return new DataRetentionOptions
        {
            SingleUseTokenGrace = ReadBoundedTimeSpan(
                SingleUseTokenGraceDaysVariable, DefaultSingleUseTokenGrace, TimeSpan.FromDays,
                minimum: 0, MaximumGraceDays, invalid),
            PurgeInterval = ReadBoundedTimeSpan(
                PurgeIntervalMinutesVariable, DefaultPurgeInterval, TimeSpan.FromMinutes,
                MinimumIntervalMinutes, MaximumIntervalMinutes, invalid),
            PurgeBatchSize = ReadPositiveInt(PurgeBatchSizeVariable, DefaultPurgeBatchSize, invalid),
            PurgeEnabled = ReadBool(PurgeEnabledVariable, defaultValue: true, invalid),
            SubjectErasureDeadline = ReadBoundedTimeSpan(
                SubjectErasureDeadlineDaysVariable, DefaultSubjectErasureDeadline, TimeSpan.FromDays,
                minimum: 0, MaximumErasureDeadlineDays, invalid),
            AdministrativeDeletionWindow = ReadBoundedTimeSpan(
                AdministrativeDeletionWindowDaysVariable, DefaultAdministrativeDeletionWindow,
                TimeSpan.FromDays, minimum: 0, MaximumDeletionWindowDays, invalid),
            AnonymisationEnabled = ReadBool(AnonymisationEnabledVariable, defaultValue: true, invalid),
            AuditActorRetention = ReadBoundedTimeSpan(
                AuditActorRetentionDaysVariable, DefaultAuditActorRetention, TimeSpan.FromDays,
                MinimumAuditActorRetentionDays, MaximumAuditActorRetentionDays, invalid),
            AuditPseudonymisationEnabled = ReadBool(
                AuditPseudonymisationEnabledVariable, defaultValue: true, invalid),
            LogRetention = ReadBoundedTimeSpan(
                LogRetentionDaysVariable, DefaultLogRetention, TimeSpan.FromDays,
                MinimumLogRetentionDays, MaximumLogRetentionDays, invalid),
            SecurityMonitoringEnabled = ReadBool(
                SecurityMonitoringEnabledVariable, defaultValue: true, invalid),
            MonitoringWindow = ReadBoundedTimeSpan(
                MonitoringWindowMinutesVariable, DefaultMonitoringWindow, TimeSpan.FromMinutes,
                MinimumMonitoringWindowMinutes, MaximumMonitoringWindowMinutes, invalid),
            RefusalThreshold = ReadPositiveInt(RefusalThresholdVariable, DefaultRefusalThreshold, invalid),
            LockoutThreshold = ReadPositiveInt(LockoutThresholdVariable, DefaultLockoutThreshold, invalid),
            InvalidVariables = invalid
        };
    }

    /// <summary>
    ///     Reads a period expressed in <paramref name="unit" />, accepting the raw number only when
    ///     it is greater than <paramref name="minimum" /> and no greater than
    ///     <paramref name="maximum" /> — both given in the variable's own unit, days or minutes.
    /// </summary>
    /// <remarks>
    ///     The bounds are checked on the number, before it is converted, and that ordering is the
    ///     point: <see cref="TimeSpan.FromDays" /> throws on a large enough <c>double</c>, so a
    ///     check performed on the converted value would never run. An exception here would fail
    ///     start-up, which is the one outcome this type exists to avoid.
    /// </remarks>
    private static TimeSpan ReadBoundedTimeSpan(
        string variable,
        TimeSpan defaultValue,
        Func<double, TimeSpan> unit,
        double minimum,
        double maximum,
        List<string> invalid)
    {
        var raw = Environment.GetEnvironmentVariable(variable);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        // A zero or negative period would purge rows the moment they expire, or run continuously;
        // both are mistakes rather than policies.
        if (!double.TryParse(raw, out var value) || value <= minimum || value > maximum)
        {
            invalid.Add(variable);

            return defaultValue;
        }

        return unit(value);
    }

    private static int ReadPositiveInt(string variable, int defaultValue, List<string> invalid)
    {
        var raw = Environment.GetEnvironmentVariable(variable);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, out var value) || value <= 0)
        {
            invalid.Add(variable);

            return defaultValue;
        }

        return value;
    }

    private static bool ReadBool(string variable, bool defaultValue, List<string> invalid)
    {
        var raw = Environment.GetEnvironmentVariable(variable);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!bool.TryParse(raw, out var value))
        {
            invalid.Add(variable);

            return defaultValue;
        }

        return value;
    }
}
