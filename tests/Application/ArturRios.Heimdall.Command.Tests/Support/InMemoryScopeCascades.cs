using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;

namespace ArturRios.Heimdall.Command.Tests.Support;

/// <summary>
///     <see cref="IScopeCascades" /> over the fake repositories a test's handler reads, applying each
///     statement's condition and write to the instances they hold, as the database applies them to the rows.
///     That the statements are set-based, batched and never load a member is the functional suite's to show
///     (<c>ScopeDeleteCascadeTests</c>).
/// </summary>
public sealed class InMemoryScopeCascades(
    IAsyncRepository<Person, long> persons,
    IAsyncRepository<GoogleUser, long> googleUsers,
    IAsyncRepository<Application, long> applications) : IScopeCascades
{
    public Task MarkMembersDeletedAsync(long scopeId, DateTime at)
    {
        const int administrative = (int)DeletionKinds.Administrative;

        foreach (var person in persons.Query().Where(x => !x.IsDeleted && x.ScopeMembership != null && x.ScopeMembership.ScopeId == scopeId))
        {
            person.IsDeleted = true;
            person.DeletedAt = at;
            person.DeletionKind = administrative;
            person.UpdatedAt = at;
        }

        foreach (var googleUser in googleUsers.Query().Where(x => x.ScopeId == scopeId && !x.IsDeleted))
        {
            googleUser.IsDeleted = true;
            googleUser.DeletedAt = at;
            googleUser.DeletionKind = administrative;
            googleUser.UpdatedAt = at;
        }

        foreach (var application in applications.Query().Where(x => x.ScopeId == scopeId && !x.IsDeleted))
        {
            application.IsDeleted = true;
            application.UpdatedAt = at;
        }

        return Task.CompletedTask;
    }

    public async Task DeleteMembersAsync(ScopeMemberIds members)
    {
        await applications.DeleteRangeAsync(members.ApplicationIds);
        await googleUsers.DeleteRangeAsync(members.GoogleUserIds);
        await persons.DeleteRangeAsync(members.PersonIds);
    }
}
