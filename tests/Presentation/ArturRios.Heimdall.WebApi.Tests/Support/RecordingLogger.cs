using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     A logger that keeps every entry it is given, rendered through its template, so a test can
///     assert exactly which lines a class writes and at which level.
/// </summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly Lock _gate = new();

    /// <summary>Each entry written, in order: its level and its rendered message.</summary>
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
