using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="DeleteScopeCommand" /> (UC-04): locates the scope (AF-04a), then logically
///     deletes it and cascades <c>IsDeleted = true</c> to its Users (via <c>SCOPE_USER</c>), Google
///     Users, and applications. Owners (<c>SCOPE_OWNER</c>) are never modified. An already-deleted
///     scope is answered as deleted (AF-04b), its cascade re-run for any member still live. The
///     response reports the totals of the scope's members, counted regardless of their individual
///     deletion state. All failures are returned as errors on the <see cref="DataOutput{T}" /> or
///     thrown, and none of them keeps a partial cascade.
/// </summary>
/// <remarks>
///     <para>
///         <b>All or nothing</b> (#204). The scope's flag and the cascade are written in one
///         <see cref="IWriteTransactions" /> transaction, so a failure part-way leaves the scope and every
///         member as they were, and the next attempt starts from the beginning. They used to be separate
///         saves: a failure after the flag left a deleted scope with live members, and the retry, finding
///         the scope deleted, skipped the cascade and answered <c>200</c>.
///     </para>
///     <para>
///         <b>A retry finishes the cascade.</b> AF-04b leaves the scope as it is but runs the cascade
///         again, which touches only members not yet deleted, so a scope left half-cascaded — by a
///         version before the transaction — is repaired by deleting it again, and an intact one costs
///         three statements that change nothing.
///     </para>
///     <para>
///         <b>Set-based</b> (<see cref="IScopeCascades" />). The members are never loaded: the cascade is
///         one conditional <c>UPDATE</c> per kind of member, and the totals are counts. A tenant of any
///         size costs the request the same memory.
///     </para>
/// </remarks>
public class DeleteScopeCommandHandler(
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IAsyncRepository<Scope, long> scopeWriter,
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<GoogleUser, long> googleUserReader,
    IAsyncReadOnlyRepository<Application, long> applicationReader,
    IScopeCascades scopeCascades,
    IWriteTransactions transactions)
    : ICommandHandlerAsync<DeleteScopeCommand, DeleteScopeCommandOutput>
{
    public async Task<DataOutput<DeleteScopeCommandOutput?>> HandleAsync(DeleteScopeCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<DeleteScopeCommandOutput?>.New;

        // Step 2 (AF-04a): locate the scope in ANY deletion state, so an already-deleted scope is
        // handled idempotently (AF-04b) rather than reported as not found.
        var scope = await scopeReader.Query().FirstOrDefaultAsync(x => x.PublicId == command.Id);

        if (scope is null)
        {
            return output.WithError(ScopeMessages.ScopeNotFound);
        }

        var now = DateTime.UtcNow;

        // Steps 3-4 land together or not at all (#204): leaving this block by any return or exception
        // before the commit rolls both back.
        await using var transaction = await transactions.BeginAsync();

        // Step 3: flip the scope itself. AF-04b: an already-deleted scope is left as it is.
        if (!scope.IsDeleted)
        {
            scope.IsDeleted = true;
            scope.UpdatedAt = now;

            var scopeUpdate = await scopeWriter.UpdateAsync(scope);

            if (!scopeUpdate.Success)
            {
                return output.WithErrors(scopeUpdate.Errors);
            }
        }

        // Step 4, for a new deletion and for AF-04b alike: every member not yet deleted. It is an
        // administrative deletion like any other (NFR-20): nobody asked to be erased, so the identities
        // it sweeps up get the reversal window rather than the statutory deadline. Members already
        // deleted keep the time and kind of their own deletion (#237).
        await scopeCascades.MarkMembersDeletedAsync(scope.Id, now);

        // Step 5's totals, counted regardless of individual deletion state.
        var userCount = await personReader.Query()
            .CountAsync(p => p.ScopeMembership != null && p.ScopeMembership.ScopeId == scope.Id);
        var googleUserCount = await googleUserReader.Query().CountAsync(g => g.ScopeId == scope.Id);
        var applicationCount = await applicationReader.Query().CountAsync(a => a.ScopeId == scope.Id);

        await transaction.CommitAsync();

        // Step 5: return the scope id and the member totals.
        return output
            .WithData(new DeleteScopeCommandOutput
            {
                Id = scope.PublicId,
                UserCount = userCount,
                GoogleUserCount = googleUserCount,
                ApplicationCount = applicationCount
            })
            .WithMessage(ScopeMessages.ScopeDeletedSuccessfully);
    }
}
