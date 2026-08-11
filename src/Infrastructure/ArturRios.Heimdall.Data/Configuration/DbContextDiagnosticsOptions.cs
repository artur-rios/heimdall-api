namespace ArturRios.Heimdall.Data.Configuration;

/// <summary>
///     Controls the EF Core diagnostics that expose data values in logs and exception messages.
///     Both flags default to <c>false</c>, so an environment we fail to classify is treated as
///     production and leaks nothing.
/// </summary>
public sealed class DbContextDiagnosticsOptions
{
    /// <summary>Diagnostics fully disabled — the production-safe default.</summary>
    public static readonly DbContextDiagnosticsOptions Disabled = new();

    /// <summary>Whether query parameter values — password hashes, salts, e-mails — may be logged.</summary>
    public bool SensitiveDataLogging { get; init; }

    /// <summary>Whether column values may be included in EF exception messages.</summary>
    public bool DetailedErrors { get; init; }
}
