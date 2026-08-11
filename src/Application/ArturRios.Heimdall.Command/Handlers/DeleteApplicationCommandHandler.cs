using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="DeleteApplicationCommand" /> (UC-19, FR-AP-07): locates the application
///     inside the addressed scope (AF-19a), enforces the acting role's rule — a System Admin may
///     delete any application, anyone else only the ones they own (AF-19c) — serves an
///     already-deleted application as an idempotent no-op (AF-19b), and otherwise sets
///     <c>IsDeleted = true</c> and stamps <c>UpdatedAt</c>. Nothing cascades: an application owns no
///     dependent row. All failures are returned as errors on the <see cref="DataOutput{T}" /> rather
///     than thrown.
/// </summary>
public class DeleteApplicationCommandHandler(
    IAsyncReadOnlyRepository<Application> applicationReader,
    IAsyncRepository<Application> applicationWriter)
    : ICommandHandlerAsync<DeleteApplicationCommand, DeleteApplicationCommandOutput>
{
    public async Task<DataOutput<DeleteApplicationCommandOutput?>> HandleAsync(DeleteApplicationCommand command)
    {
        var output = DataOutput<DeleteApplicationCommandOutput?>.New;

        // AF-19a: the application must exist inside the addressed scope. The lookup deliberately omits
        // the !IsDeleted filter UC-18 applies, so an already-deleted application is found and served
        // idempotently by AF-19b below rather than reported as not found. The route's scopeId
        // qualifies it: an application that lives in another scope is not the resource this path
        // addresses. Checked before authorization so AF-19a and AF-19c both stay observable.
        var application = await applicationReader.Query()
            .Include(x => x.Owner)
            .FirstOrDefaultAsync(x => x.PublicId == command.Id && x.Scope.PublicId == command.ScopeId);

        if (application is null)
        {
            return output.WithError(ApplicationMessages.ApplicationNotFound);
        }

        // AF-19c (UC-19 step 2): a System Admin may delete any application; anyone else must own it.
        // Owning the *scope* is not by itself grounds to delete another owner's application, so the
        // rule compares the owner rather than consulting IScopeOwnershipChecker — the same call UC-17
        // makes for reads and UC-18 for updates. Runs before AF-19b so an already-deleted application
        // cannot be used to probe for applications outside the caller's reach.
        if (command.ActingRole != (int)Roles.SystemAdmin && application.Owner.PublicId != command.ActingPersonId)
        {
            return output.WithError(ApplicationMessages.NotAuthorizedToDeleteApplication);
        }

        // AF-19b: already deleted — whether directly or by UC-04's cascade from its scope — so there
        // is nothing to write. UpdatedAt is left alone: the row already carries the requested state,
        // and re-stamping it would misreport when the deletion happened.
        if (application.IsDeleted)
        {
            return output
                .WithData(new DeleteApplicationCommandOutput { Id = application.PublicId, AlreadyDeleted = true })
                .WithMessage(ApplicationMessages.ApplicationDeletedSuccessfully);
        }

        // UC-19 step 3: flip the flag and stamp UpdatedAt (no DB trigger maintains it).
        application.IsDeleted = true;
        application.UpdatedAt = DateTime.UtcNow;

        var update = await applicationWriter.UpdateAsync(application);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        // UC-19 step 4.
        return output
            .WithData(new DeleteApplicationCommandOutput { Id = application.PublicId, AlreadyDeleted = false })
            .WithMessage(ApplicationMessages.ApplicationDeletedSuccessfully);
    }
}
