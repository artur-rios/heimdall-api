using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     Runs an action once, just before the context's first <c>UPDATE</c> reaches the database, so a race
///     test can land a competing write between what the code under test read and what it writes (see
///     <c>ApplicationCredentialRaceTests</c> and <c>ScopeOwnerInvitationsTests</c>).
/// </summary>
/// <remarks>
///     The action should write through its own connection and commit before it returns; the intercepted
///     <c>UPDATE</c> is sent only after that.
/// </remarks>
public sealed class BeforeFirstUpdate(Func<Task> action) : DbCommandInterceptor
{
    private bool _fired;

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await FireBeforeUpdateAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await FireBeforeUpdateAsync(command);
        return result;
    }

    private async Task FireBeforeUpdateAsync(DbCommand command)
    {
        if (_fired || !command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _fired = true;
        await action();
    }
}
