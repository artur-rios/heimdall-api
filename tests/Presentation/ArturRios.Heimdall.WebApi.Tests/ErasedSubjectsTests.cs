using ArturRios.Heimdall.Data.Persistence;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     The tombstone write every hard delete makes first (NFR-21, #195), against real PostgreSQL.
/// </summary>
/// <remarks>
///     The unit tests run the hard-delete handlers over an in-memory stand-in that only promises the
///     contract. This proves the real upsert keeps it: a retried hard delete records the same id again
///     without failing on the primary key, and still leaves one row.
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class ErasedSubjectsTests(PostgresFixture fixture)
{
    private static ErasedSubject Tombstone(Guid id, ErasedSubjectKinds kind, DateTime createdAt) =>
        new() { Id = id, Kind = (int)kind, CreatedAt = createdAt };

    private async Task<List<ErasedSubject>> StoredAsync(params Guid[] ids)
    {
        await using var context = fixture.CreateContext();

        return await context.ErasedSubjects.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync();
    }

    [FunctionalFact]
    public async Task GivenNewIds_WhenRecorded_ThenEachIsStoredOnceWithItsKind()
    {
        // Given a person and a Google User removed in one hard delete
        var person = Guid.NewGuid();
        var googleUser = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // When
        await using (var context = fixture.CreateContext())
        {
            await new ErasedSubjects(context).RecordAsync(
            [
                Tombstone(person, ErasedSubjectKinds.Person, now),
                Tombstone(googleUser, ErasedSubjectKinds.GoogleUser, now)
            ]);
        }

        // Then
        var stored = (await StoredAsync(person, googleUser)).ToDictionary(x => x.Id);
        Assert.Equal(2, stored.Count);
        Assert.Equal((int)ErasedSubjectKinds.Person, stored[person].Kind);
        Assert.Equal((int)ErasedSubjectKinds.GoogleUser, stored[googleUser].Kind);
    }

    [FunctionalFact]
    public async Task GivenAnIdAlreadyRecorded_WhenRecordedAgain_ThenItIsNotAnErrorAndOneRowRemains()
    {
        // Given a tombstone left by an attempt that then failed to delete the row
        var person = Guid.NewGuid();
        var earlier = DateTime.UtcNow.AddDays(-3);

        await using (var context = fixture.CreateContext())
        {
            await new ErasedSubjects(context).RecordAsync([Tombstone(person, ErasedSubjectKinds.Person, earlier)]);
        }

        // When the retry records it again
        var retry = DateTime.UtcNow;
        Exception? failure;

        await using (var context = fixture.CreateContext())
        {
            failure = await Record.ExceptionAsync(() =>
                new ErasedSubjects(context).RecordAsync([Tombstone(person, ErasedSubjectKinds.Person, retry)]));
        }

        // Then — no primary-key failure, one row, its grace counted from the retry
        Assert.Null(failure);
        var stored = Assert.Single(await StoredAsync(person));
        Assert.True(stored.CreatedAt > earlier.AddDays(1));
    }

    [FunctionalFact]
    public async Task GivenTheSameIdTwiceInOneCall_WhenRecorded_ThenOneRowIsStored()
    {
        // Given — ON CONFLICT cannot touch one row twice in a statement, so duplicates must not reach it
        var person = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // When
        await using (var context = fixture.CreateContext())
        {
            await new ErasedSubjects(context).RecordAsync(
            [
                Tombstone(person, ErasedSubjectKinds.Person, now),
                Tombstone(person, ErasedSubjectKinds.Person, now)
            ]);
        }

        // Then
        Assert.Single(await StoredAsync(person));
    }
}
