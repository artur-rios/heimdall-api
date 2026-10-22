using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     Fails one command of a context, as a database fault would: the <paramref name="occurrence" />th
///     command that <paramref name="verb" /> (<c>UPDATE</c>, <c>INSERT</c>, <c>DELETE</c>) writes to the
///     table <c>heimdall.<paramref name="table" /></c> throws <see cref="InjectedDatabaseFault" />
///     instead of reaching the database. Every command before and after it runs as usual.
/// </summary>
/// <remarks>
///     For the tests of what a command leaves behind when one of its writes fails part-way (#209, #212,
///     #204). The fault is raised before the command is sent, so the database never sees it: what the
///     test observes afterwards is only what the code under test committed or rolled back.
/// </remarks>
public sealed class FailingCommand(string verb, string table, int occurrence = 1) : DbCommandInterceptor
{
    // The table the command writes: the one right after UPDATE, INSERT INTO or DELETE FROM, not one it
    // only reads in a subquery.
    private readonly Regex _target = new(
        $@"^{Regex.Escape(verb)}\s+(INTO\s+|FROM\s+)?heimdall\.{Regex.Escape(table)}\b", RegexOptions.IgnoreCase);
    private int _seen;

    /// <summary>Whether the fault was raised: a test asserts it, so it cannot pass by missing its target.</summary>
    public bool Fired { get; private set; }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        FailIfTargeted(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        FailIfTargeted(command);
        return ValueTask.FromResult(result);
    }

    private void FailIfTargeted(DbCommand command)
    {
        if (Fired || !_target.IsMatch(command.CommandText.TrimStart()))
        {
            return;
        }

        if (++_seen < occurrence)
        {
            return;
        }

        Fired = true;
        throw new InjectedDatabaseFault();
    }
}

/// <summary>The fault <see cref="FailingCommand" /> raises: a <see cref="DbException" />, as the provider's own are.</summary>
public sealed class InjectedDatabaseFault() : DbException("Injected database fault");
