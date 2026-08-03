using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Command.Handlers;

/// <summary>
///     Handles <see cref="SetGoogleSignInCommand" /> (UC-24, FR-GO-01/FR-GO-02): validates that the
///     request said which value to set, verifies the target scope exists and is active (AF-24a),
///     enforces scope ownership for a Scope Admin actor (AF-24b), then writes the scope's
///     <c>GoogleSignInEnabled</c> flag and returns the updated scope. A System Admin actor bypasses
///     the ownership check. All failures are returned as errors on the <see cref="DataOutput{T}" />
///     rather than thrown, using the canonical <see cref="ScopeMessages" /> so the response resolver
///     can pick the matching status code.
/// </summary>
public class SetGoogleSignInCommandHandler(
    IValidator<SetGoogleSignInCommand> validator,
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncRepository<Scope> scopeWriter,
    IScopeOwnershipChecker scopeOwnership)
    : ICommandHandlerAsync<SetGoogleSignInCommand, SetGoogleSignInCommandOutput>
{
    public async Task<DataOutput<SetGoogleSignInCommandOutput?>> HandleAsync(SetGoogleSignInCommand command)
    {
        var output = DataOutput<SetGoogleSignInCommandOutput?>.New;

        // NFR-10: the request must state which value to set. Enabled is nullable precisely so this
        // check is possible — a plain bool would bind a body omitting the field to false, turning a
        // malformed request into the destructive half of the toggle.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-24a (UC-24 step 2): the target scope must exist and not be logically deleted — the
        // alternative flow names both conditions as one outcome, so the filter answers for both.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.Id && !x.IsDeleted);

        if (scope is null)
        {
            return output.WithError(ScopeMessages.ScopeNotFound);
        }

        // AF-24b (UC-24 step 3): a Scope Admin actor may only act on a scope they own; a System Admin
        // bypasses. Checked before anything is written, so a refused caller leaves no trace.
        if (!await scopeOwnership.ActorMayManageScopeAsync(command.ActingRole, command.ActingPersonId, scope.Id))
        {
            return output.WithError(ScopeMessages.NotScopeOwner);
        }

        // Owner PublicIds for the response. A projection over the navigation (no Include needed),
        // which EF translates to a join and the in-memory fake evaluates directly.
        var ownerIds = await scopeReader.Query()
            .Where(x => x.PublicId == command.Id)
            .SelectMany(x => x.Owners.Select(owner => owner.Person.PublicId))
            .ToListAsync();

        // UC-24 step 4: write the flag and stamp UpdatedAt (no DB trigger maintains it). Written
        // unconditionally: PUT is idempotent by contract and UC-24 defines no alternative flow for a
        // request that asks for the value the scope already holds.
        scope.GoogleSignInEnabled = command.Enabled!.Value;
        scope.UpdatedAt = DateTime.UtcNow;

        var update = await scopeWriter.UpdateAsync(scope);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        // UC-24 step 5: return the updated scope.
        return output
            .WithData(new SetGoogleSignInCommandOutput
            {
                Id = scope.PublicId,
                Name = scope.Name,
                Description = scope.Description,
                GoogleSignInEnabled = scope.GoogleSignInEnabled,
                OwnerIds = ownerIds,
                CreatedAt = scope.CreatedAt,
                UpdatedAt = scope.UpdatedAt
            })
            .WithMessage(ScopeMessages.GoogleSignInUpdatedSuccessfully);
    }
}
