using ArturRios.Heimdall.Command.Services;

namespace ArturRios.Heimdall.Command.Tests.Support;

/// <summary><see cref="IRetryAfterAdvisor" /> that records the waits a handler advised.</summary>
public sealed class RecordingRetryAfterAdvisor : IRetryAfterAdvisor
{
    public List<TimeSpan> Advised { get; } = [];

    public void Advise(TimeSpan wait) => Advised.Add(wait);
}
