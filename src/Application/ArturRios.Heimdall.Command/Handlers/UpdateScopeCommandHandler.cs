using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="UpdateScopeCommand" /> (UC-03): validates the request, verifies the scope
///     exists and is not logically deleted (AF-03a), verifies the new name does not collide with
///     another scope (AF-03b), then applies the changes and stamps <c>UpdatedAt</c>. All failures are
///     returned as errors on the <see cref="DataOutput{T}" /> rather than thrown, using the canonical
///     <see cref="ScopeMessages" /> so the response resolver can pick the matching status code.
/// </summary>
public class UpdateScopeCommandHandler(
    IValidator<UpdateScopeCommand> validator,
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncRepository<Scope> scopeWriter)
    : ICommandHandlerAsync<UpdateScopeCommand, UpdateScopeCommandOutput>
{
    public async Task<DataOutput<UpdateScopeCommandOutput?>> HandleAsync(UpdateScopeCommand command)
    {
        var output = DataOutput<UpdateScopeCommandOutput?>.New;

        // Step 2: validate input fields.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // Step 3 (AF-03a): the scope must exist and not be logically deleted.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.Id && !x.IsDeleted);

        if (scope is null)
        {
            return output.WithError(ScopeMessages.ScopeNotFound);
        }

        // Step 4 (AF-03b): the new name must not collide with another scope, compared
        // case-insensitively (LOWER() in SQL) so the app layer authoritatively rejects case variants
        // that the case-sensitive DB unique index on Name would not catch. Checked against all scopes
        // (deleted included), excluding this scope so an unchanged name is not a false conflict.
        var nameTaken = await scopeReader.Query()
            .AnyAsync(x => x.Name.ToLower() == command.Name.ToLower() && x.PublicId != command.Id);

        if (nameTaken)
        {
            return output.WithError(ScopeMessages.NameAlreadyExists);
        }

        // Owner PublicIds for the response. A projection over the navigation (no Include needed),
        // which EF translates to a join and the in-memory fake evaluates directly.
        var ownerIds = await scopeReader.Query()
            .Where(x => x.PublicId == command.Id)
            .SelectMany(x => x.Owners.Select(owner => owner.Person.PublicId))
            .ToListAsync();

        // Step 4 (main flow): apply the updates and stamp UpdatedAt (no DB trigger maintains it).
        scope.Name = command.Name;
        scope.Description = command.Description;
        scope.UpdatedAt = DateTime.UtcNow;

        var update = await scopeWriter.UpdateAsync(scope);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        // Step 5: return the updated scope.
        return output
            .WithData(new UpdateScopeCommandOutput
            {
                Id = scope.PublicId,
                Name = scope.Name,
                Description = scope.Description,
                GoogleSignInEnabled = scope.GoogleSignInEnabled,
                OwnerIds = ownerIds,
                CreatedAt = scope.CreatedAt,
                UpdatedAt = scope.UpdatedAt
            })
            .WithMessage(ScopeMessages.ScopeUpdatedSuccessfully);
    }
}
