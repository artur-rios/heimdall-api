using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     Records the text of every command a context sends, so a test can assert how the code under test
///     talks to the database — how many statements a cascade takes, which columns it reads — and not only
///     what the database holds afterwards (see <c>ScopeDeleteCascadeTests</c>).
/// </summary>
public sealed class CommandLog : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _commands = new();

    /// <summary>Every command sent, in order, leading whitespace removed.</summary>
    public IReadOnlyCollection<string> Commands => _commands;

    /// <summary>The commands that start with <paramref name="prefix" />, such as <c>DELETE FROM heimdall.person</c>.</summary>
    public int CountStartingWith(string prefix) =>
        _commands.Count(command => command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText.TrimStart());
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText.TrimStart());
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText.TrimStart());
        return ValueTask.FromResult(result);
    }
}
