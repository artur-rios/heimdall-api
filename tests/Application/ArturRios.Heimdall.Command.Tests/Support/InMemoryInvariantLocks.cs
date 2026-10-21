using ArturRios.Heimdall.Domain.Persistence;

namespace ArturRios.Heimdall.Command.Tests.Support;

/// <summary>
///     <see cref="IInvariantLocks" /> for handler tests over the fake repositories: it locks nothing, since a
///     handler test runs one request at a time, but it records what a handler asked to lock, so a test can show
///     the right scopes are locked. The functional tests prove the locks themselves against PostgreSQL.
/// </summary>
public sealed class InMemoryInvariantLocks : IInvariantLocks
{
    /// <summary>Shared by tests that do not look at what was locked; its record means nothing.</summary>
    public static readonly InMemoryInvariantLocks Instance = new();

    private readonly List<IReadOnlyCollection<long>> _scopeLocks = [];

    /// <summary>How many <see cref="LockAsync" /> calls also locked the System Admin set.</summary>
    public int SystemAdminLocks { get; private set; }

    /// <summary>The scope ids of each <see cref="LockAsync" /> call, in order.</summary>
    public IReadOnlyList<IReadOnlyCollection<long>> ScopeLocks
    {
        get
        {
            lock (_scopeLocks)
            {
                return _scopeLocks.ToList();
            }
        }
    }

    public Task LockAsync(IReadOnlyCollection<long> scopeIds, bool systemAdmins = false)
    {
        lock (_scopeLocks)
        {
            _scopeLocks.Add(scopeIds.ToList());

            if (systemAdmins)
            {
                SystemAdminLocks++;
            }
        }

        return Task.CompletedTask;
    }
}
