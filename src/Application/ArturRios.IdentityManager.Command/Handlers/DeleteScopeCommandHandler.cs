using ArturRios.Data.Relational.Core.Entities;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Command.Handlers;

/// <summary>
///     Handles <see cref="DeleteScopeCommand" /> (UC-04): locates the scope (AF-04a), then logically
///     deletes it and cascades <c>IsDeleted = true</c> to its Users (via <c>SCOPE_USER</c>), Google
///     Users, and applications. Owners (<c>SCOPE_OWNER</c>) are never modified. An already-deleted
///     scope is an idempotent no-op (AF-04b). The response reports the totals of the scope's members,
///     counted regardless of their individual deletion state. All failures are returned as errors on
///     the <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
public class DeleteScopeCommandHandler(
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncRepository<Scope> scopeWriter,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Person> personWriter,
    IAsyncReadOnlyRepository<GoogleUser> googleUserReader,
    IAsyncRepository<GoogleUser> googleUserWriter,
    IAsyncReadOnlyRepository<Application> applicationReader,
    IAsyncRepository<Application> applicationWriter)
    : ICommandHandlerAsync<DeleteScopeCommand, DeleteScopeCommandOutput>
{
    public async Task<DataOutput<DeleteScopeCommandOutput?>> HandleAsync(DeleteScopeCommand command)
    {
        var output = DataOutput<DeleteScopeCommandOutput?>.New;

        // Step 2 (AF-04a): locate the scope in ANY deletion state, so an already-deleted scope is
        // handled idempotently (AF-04b) rather than reported as not found.
        var scope = await scopeReader.Query().FirstOrDefaultAsync(x => x.PublicId == command.Id);

        if (scope is null)
        {
            return output.WithError(ScopeMessages.ScopeNotFound);
        }

        // Step 3: the scope's members, counted regardless of individual deletion state (both flows).
        var users = await personReader.Query()
            .Where(p => p.ScopeMembership != null && p.ScopeMembership.ScopeId == scope.Id)
            .ToListAsync();
        var googleUsers = await googleUserReader.Query()
            .Where(g => g.ScopeId == scope.Id)
            .ToListAsync();
        var applications = await applicationReader.Query()
            .Where(a => a.ScopeId == scope.Id)
            .ToListAsync();

        // AF-04b: an already-deleted scope is left untouched; the totals are still reported below.
        if (!scope.IsDeleted)
        {
            var now = DateTime.UtcNow;

            // Step 4: flip the scope itself.
            scope.IsDeleted = true;
            scope.UpdatedAt = now;

            var scopeUpdate = await scopeWriter.UpdateAsync(scope);

            if (!scopeUpdate.Success)
            {
                return output.WithErrors(scopeUpdate.Errors);
            }

            // Step 5: cascade to the members that are not already deleted.
            var cascadeErrors = (await CascadeAsync(users, p => p.IsDeleted,
                    p => { p.IsDeleted = true; p.UpdatedAt = now; }, personWriter))
                .Concat(await CascadeAsync(googleUsers, g => g.IsDeleted,
                    g => { g.IsDeleted = true; g.UpdatedAt = now; }, googleUserWriter))
                .Concat(await CascadeAsync(applications, a => a.IsDeleted,
                    a => { a.IsDeleted = true; a.UpdatedAt = now; }, applicationWriter))
                .ToList();

            if (cascadeErrors.Count > 0)
            {
                return output.WithErrors(cascadeErrors);
            }
        }

        // Step 6: return the scope id and the member totals.
        return output
            .WithData(new DeleteScopeCommandOutput
            {
                Id = scope.PublicId,
                UserCount = users.Count,
                GoogleUserCount = googleUsers.Count,
                ApplicationCount = applications.Count
            })
            .WithMessage(ScopeMessages.ScopeDeletedSuccessfully);
    }

    /// <summary>
    ///     Flips <c>IsDeleted</c> (and <c>UpdatedAt</c>, via <paramref name="markDeleted" />) on the
    ///     members that are not already deleted, then persists them. Returns any persistence errors,
    ///     or an empty sequence when there is nothing to update.
    /// </summary>
    private static async Task<IEnumerable<string>> CascadeAsync<T>(
        IEnumerable<T> members,
        Func<T, bool> isDeleted,
        Action<T> markDeleted,
        IAsyncRepository<T> writer) where T : Entity
    {
        var pending = members.Where(member => !isDeleted(member)).ToList();

        if (pending.Count == 0)
        {
            return [];
        }

        foreach (var member in pending)
        {
            markDeleted(member);
        }

        var result = await writer.UpdateRangeAsync(pending);

        return result.Success ? [] : result.Errors;
    }
}
