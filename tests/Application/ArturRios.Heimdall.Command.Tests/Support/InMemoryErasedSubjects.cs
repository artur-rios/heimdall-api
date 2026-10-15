using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;

namespace ArturRios.Heimdall.Command.Tests.Support;

/// <summary>
///     <see cref="IErasedSubjects" /> over a list, with the same contract as the PostgreSQL upsert: one
///     row per id, a repeated id refreshing <c>CreatedAt</c> rather than failing.
/// </summary>
/// <remarks>
///     <paramref name="beforeRecord" /> runs on every call before anything is stored, so a test can
///     look at the other fakes at the moment the tombstones are written — which is how the hard-delete
///     tests prove they are written before the rows go.
/// </remarks>
public sealed class InMemoryErasedSubjects(Action<IReadOnlyCollection<ErasedSubject>>? beforeRecord = null)
    : IErasedSubjects
{
    private readonly List<ErasedSubject> _recorded = [];

    /// <summary>Every tombstone held, one per id.</summary>
    public IReadOnlyList<ErasedSubject> Recorded => _recorded;

    /// <summary>How many times <see cref="RecordAsync" /> was called.</summary>
    public int Calls { get; private set; }

    public Task RecordAsync(IReadOnlyCollection<ErasedSubject> subjects)
    {
        Calls++;
        beforeRecord?.Invoke(subjects);

        foreach (var subject in subjects)
        {
            var existing = _recorded.SingleOrDefault(x => x.Id == subject.Id);

            if (existing is null)
            {
                _recorded.Add(new ErasedSubject { Id = subject.Id, Kind = subject.Kind, CreatedAt = subject.CreatedAt });
            }
            else
            {
                existing.CreatedAt = subject.CreatedAt;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Seeds a tombstone as if an earlier, failed hard delete had written it.</summary>
    public void Seed(Guid id, int kind, DateTime createdAt) =>
        _recorded.Add(new ErasedSubject { Id = id, Kind = kind, CreatedAt = createdAt });
}
