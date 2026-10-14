using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Data.Persistence;

/// <summary>
///     <see cref="IApplicationCredentialSlots" /> on PostgreSQL: a transaction that locks the
///     application's row, counts its live credentials, and inserts only below the limit.
/// </summary>
/// <remarks>
///     A count followed by an insert is a race on its own — two requests both count one, both
///     insert, and the application holds three live secrets. <c>SELECT … FOR UPDATE</c> on the
///     application's row makes a second issuer for the same application wait until the first commits,
///     and then count the row the first inserted. Other applications are not blocked. Unlike
///     <see cref="AtomicWrites" />, this needs a transaction, because the rule spans rows a single
///     conditional <c>UPDATE</c> cannot see.
/// </remarks>
public sealed class ApplicationCredentialSlots(AppDbContext context) : IApplicationCredentialSlots
{
    public async Task<bool> TryAddWithinLimitAsync(ApplicationCredential credential, int maxLive, DateTime now)
    {
        // The table name comes from the model, never from input, so building it into the SQL is safe;
        // the id stays a parameter.
        var applicationTable = context.Model.FindEntityType(typeof(Application))!.GetSchemaQualifiedTableName();
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();

            var sql = $"SELECT 1 FROM {applicationTable} WHERE id = {{0}} FOR UPDATE";
            await context.Database.ExecuteSqlRawAsync(sql, credential.ApplicationId);

            var live = await context.ApplicationCredentials
                .Where(x => x.ApplicationId == credential.ApplicationId)
                .CountAsync(ApplicationCredential.LiveAt(now));

            if (live >= maxLive)
            {
                await transaction.RollbackAsync();

                return false;
            }

            context.ApplicationCredentials.Add(credential);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();

            return true;
        });
    }
}
