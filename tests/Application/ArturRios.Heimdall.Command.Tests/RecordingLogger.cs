using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Command.Tests;

// Keeps what a component logged — level and rendered text — so a test can assert that a failure was
// logged once, at the right level, and without an address in it (NFR-22).
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Text)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
}
