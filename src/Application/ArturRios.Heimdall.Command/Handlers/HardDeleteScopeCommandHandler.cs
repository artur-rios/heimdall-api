using ArturRios.Data.Relational.Core.Entities;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="HardDeleteScopeCommand" /> (UC-05): locates the scope (AF-05a), then
///     permanently deletes its Users (via <c>SCOPE_USER</c>), Google Users, and applications, and
///     finally the scope itself — whose <c>ON DELETE CASCADE</c> foreign keys remove the
///     <c>SCOPE_OWNER</c>/<c>SCOPE_USER</c> join rows and any remaining scope permissions. Owner
///     person records (<c>ScopeAdmin</c>s) are never removed. The response reports the totals of
///     the scope's members and permissions, counted regardless of their individual deletion state.
///     All failures are returned as errors on the <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
public class HardDeleteScopeCommandHandler(
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncRepository<Scope> scopeWriter,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Person> personWriter,
    IAsyncReadOnlyRepository<GoogleUser> googleUserReader,
    IAsyncRepository<GoogleUser> googleUserWriter,
    IAsyncReadOnlyRepository<Application> applicationReader,
    IAsyncRepository<Application> applicationWriter,
    IAsyncReadOnlyRepository<ScopePermission> scopePermissionReader)
    : ICommandHandlerAsync<HardDeleteScopeCommand, HardDeleteScopeCommandOutput>
{
    public async Task<DataOutput<HardDeleteScopeCommandOutput?>> HandleAsync(HardDeleteScopeCommand command)
    {
        var output = DataOutput<HardDeleteScopeCommandOutput?>.New;

        // Step 2 (AF-05a): locate the scope in ANY deletion state — an already logically-deleted
        // scope can still be hard-deleted.
        var scope = await scopeReader.Query().FirstOrDefaultAsync(x => x.PublicId == command.Id);

        if (scope is null)
        {
            return output.WithError(ScopeMessages.ScopeNotFound);
        }

        // Step 3: the scope's members, counted regardless of individual deletion state.
        var users = await personReader.Query()
            .Where(p => p.ScopeMembership != null && p.ScopeMembership.ScopeId == scope.Id)
            .ToListAsync();
        var googleUsers = await googleUserReader.Query()
            .Where(g => g.ScopeId == scope.Id)
            .ToListAsync();
        var applications = await applicationReader.Query()
            .Where(a => a.ScopeId == scope.Id)
            .ToListAsync();
        var scopePermissionCount = await scopePermissionReader.Query()
            .CountAsync(p => p.ScopeId == scope.Id);

        // Step 4: delete the members explicitly, in an order that never violates a foreign key
        // (applications reference their owning person, so they go first).
        var deleteErrors = (await DeleteAllAsync(applications, applicationWriter))
            .Concat(await DeleteAllAsync(googleUsers, googleUserWriter))
            .Concat(await DeleteAllAsync(users, personWriter))
            .ToList();

        if (deleteErrors.Count > 0)
        {
            return output.WithErrors(deleteErrors);
        }

        // Step 5: delete the scope; its ON DELETE CASCADE foreign keys clear the SCOPE_OWNER and any
        // remaining SCOPE_USER join rows, as well as any remaining scope permissions. Owner person
        // records are untouched.
        var scopeDelete = await scopeWriter.DeleteAsync(scope);

        if (!scopeDelete.Success)
        {
            return output.WithErrors(scopeDelete.Errors);
        }

        // Step 6: return the scope id and the member/permission totals.
        return output
            .WithData(new HardDeleteScopeCommandOutput
            {
                Id = scope.PublicId,
                UserCount = users.Count,
                GoogleUserCount = googleUsers.Count,
                ApplicationCount = applications.Count,
                ScopePermissionCount = scopePermissionCount
            })
            .WithMessage(ScopeMessages.ScopeHardDeletedSuccessfully);
    }

    /// <summary>
    ///     Permanently removes every entity in <paramref name="members" /> by internal Id, or does
    ///     nothing when the set is empty. Returns any persistence errors, or an empty sequence on
    ///     success / no-op.
    /// </summary>
    private static async Task<IEnumerable<string>> DeleteAllAsync<T>(
        IReadOnlyCollection<T> members,
        IAsyncRepository<T> writer) where T : Entity
    {
        if (members.Count == 0)
        {
            return [];
        }

        var result = await writer.DeleteRangeAsync(members.Select(member => member.Id));

        return result.Success ? [] : result.Errors;
    }
}
