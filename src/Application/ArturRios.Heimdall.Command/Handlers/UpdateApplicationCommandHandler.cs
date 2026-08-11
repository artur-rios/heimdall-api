using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="UpdateApplicationCommand" /> (UC-18, FR-AP-06): validates input, loads the
///     application inside the addressed scope (AF-18a), enforces the acting role's rule — a System
///     Admin may update any application, anyone else only the ones they own (AF-18c) — verifies a new
///     owner satisfies FR-AP-03 when the owner actually changes (AF-18b), then applies the changes and
///     stamps <c>UpdatedAt</c>. All failures are returned as errors on the output rather than thrown.
/// </summary>
public class UpdateApplicationCommandHandler(
    IValidator<UpdateApplicationCommand> validator,
    IAsyncReadOnlyRepository<Application> applicationReader,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Application> applicationWriter)
    : ICommandHandlerAsync<UpdateApplicationCommand, UpdateApplicationCommandOutput>
{
    public async Task<DataOutput<UpdateApplicationCommandOutput?>> HandleAsync(UpdateApplicationCommand command)
    {
        var output = DataOutput<UpdateApplicationCommandOutput?>.New;

        // UC-18 step 2: validate input shape. UC-18 defines no alternative flow for this, so the
        // validator's UC-16 messages carry their existing 400.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-18a: the application must exist inside the addressed scope and not be logically deleted.
        // The route's scopeId qualifies the lookup, as UC-17's by-id read does: an application that
        // lives in another scope is not the resource this path addresses. Checked before
        // authorization so AF-18a and AF-18c both stay observable.
        var application = await applicationReader.Query()
            .Include(x => x.Scope)
            .Include(x => x.Owner)
            .FirstOrDefaultAsync(x =>
                x.PublicId == command.Id && x.Scope.PublicId == command.ScopeId && !x.IsDeleted);

        if (application is null)
        {
            return output.WithError(ApplicationMessages.ApplicationNotFound);
        }

        var currentOwnerId = application.Owner.PublicId;

        // AF-18c (UC-18 step 3): a System Admin may update any application; anyone else must own it.
        // Owning the *scope* is not by itself grounds to modify another owner's application, so the
        // rule compares the owner rather than consulting IScopeOwnershipChecker — the same call
        // UC-17 makes for reads.
        if (command.ActingRole != (int)Roles.SystemAdmin && currentOwnerId != command.ActingPersonId)
        {
            return output.WithError(ApplicationMessages.NotAuthorizedToUpdateApplication);
        }

        var newOwnerId = currentOwnerId;

        // AF-18b (UC-18 step 4): only *a change of* owner is verified. Verifying unconditionally
        // would refuse an ordinary rename whenever the existing owner had since been logically
        // deleted or lost their SCOPE_OWNER row — a refusal UC-18 does not define, and one that would
        // leave such an application uneditable.
        if (command.OwnerId != currentOwnerId)
        {
            // FR-AP-03: the new owner must be an existing, non-logically-deleted ScopeAdmin who owns
            // the application's scope. Same shape as UC-16's owner check, against the scope the
            // application already belongs to (FR-AP-02 fixes that at creation time).
            var newOwner = await personReader.Query().FirstOrDefaultAsync(person =>
                person.PublicId == command.OwnerId && !person.IsDeleted &&
                person.RoleId == (long)Roles.ScopeAdmin &&
                person.ScopeOwnerships.Any(ownership => ownership.ScopeId == application.ScopeId));

            if (newOwner is null)
            {
                return output.WithError(ApplicationMessages.OwnerNotValidForScope);
            }

            // Both the foreign key and the navigation are set, so the tracked entity carries one
            // consistent answer whichever of the two the change tracker reads.
            application.OwnerId = newOwner.Id;
            application.Owner = newOwner;
            newOwnerId = newOwner.PublicId;
        }

        // UC-18 step 5: apply the updates and stamp UpdatedAt (no DB trigger maintains it).
        application.Name = command.Name;
        application.UpdatedAt = DateTime.UtcNow;

        var update = await applicationWriter.UpdateAsync(application);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        // UC-18 step 6: return the updated application.
        return output
            .WithData(new UpdateApplicationCommandOutput
            {
                Id = application.PublicId,
                Name = application.Name,
                ScopeId = application.Scope.PublicId,
                OwnerId = newOwnerId,
                CreatedAt = application.CreatedAt,
                UpdatedAt = application.UpdatedAt
            })
            .WithMessage(ApplicationMessages.ApplicationUpdatedSuccessfully);
    }
}
