using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Data.Persistence;

/// <summary>
///     <see cref="IErasedSubjects" /> on PostgreSQL: one <c>INSERT … ON CONFLICT</c> for the whole set.
/// </summary>
/// <remarks>
///     <para>
///         A tracked insert would fail on the primary key when a retried hard delete tombstones an id
///         again, and checking first would race a concurrent retry. <c>ON CONFLICT (public_id) DO
///         UPDATE</c> makes the database settle it: a new id is inserted, a known one keeps its row and
///         takes the new <c>created_at</c>, so the grace period counts from the attempt that goes on to
///         delete the row.
///     </para>
///     <para>
///         The ids travel as arrays and are unnested, so a scope's members are tombstoned in one
///         statement however many there are, and all or none of them are. The statement bypasses the
///         change tracker; nothing in a hard delete reads the tombstones back.
///     </para>
/// </remarks>
public sealed class ErasedSubjects(AppDbContext context) : IErasedSubjects
{
    public async Task RecordAsync(IReadOnlyCollection<ErasedSubject> subjects)
    {
        if (subjects.Count == 0)
        {
            return;
        }

        // The table name comes from the model, never from input, so building it into the SQL is safe;
        // the values stay parameters.
        var table = context.Model.FindEntityType(typeof(ErasedSubject))!.GetSchemaQualifiedTableName();

        // One row per id, as ON CONFLICT cannot touch the same row twice in one statement.
        var distinct = subjects.DistinctBy(subject => subject.Id).ToList();

        var sql =
            $$"""
              INSERT INTO {{table}} (public_id, kind, created_at)
              SELECT * FROM unnest({0}::uuid[], {1}::integer[], {2}::timestamptz[])
              ON CONFLICT (public_id) DO UPDATE SET created_at = EXCLUDED.created_at
              """;

        await context.Database.ExecuteSqlRawAsync(
            sql,
            distinct.Select(subject => subject.Id).ToArray(),
            distinct.Select(subject => subject.Kind).ToArray(),
            distinct.Select(subject => subject.CreatedAt).ToArray());
    }
}
