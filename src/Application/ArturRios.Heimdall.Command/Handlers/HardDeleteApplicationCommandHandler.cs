using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="HardDeleteApplicationCommand" /> (UC-20, FR-AP-08): locates the application
///     inside the addressed scope in any deletion state (AF-20a) and permanently removes the record.
///     Nothing cascades — an application is a leaf in the data model, and the scope and owner its
///     foreign keys point at are left intact. Authorization is entirely the endpoint's: UC-20's only
///     actor is the System Admin, so no rule is left for the handler to apply. All failures are
///     returned as errors on the <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
public class HardDeleteApplicationCommandHandler(
    IAsyncReadOnlyRepository<Application> applicationReader,
    IAsyncRepository<Application> applicationWriter)
    : ICommandHandlerAsync<HardDeleteApplicationCommand, HardDeleteApplicationCommandOutput>
{
    public async Task<DataOutput<HardDeleteApplicationCommandOutput?>> HandleAsync(
        HardDeleteApplicationCommand command)
    {
        var output = DataOutput<HardDeleteApplicationCommandOutput?>.New;

        // AF-20a: the application must exist inside the addressed scope. The lookup omits an
        // !IsDeleted filter — a logically deleted application, whether by UC-19 or by UC-04's cascade
        // from its scope, is exactly what a cleanup pass starts from and must still be purgeable, the
        // same call UC-05 and UC-10 make. The route's scopeId qualifies it: an unknown application,
        // an unknown scope, and an application living in another scope are all one 404, because the
        // addressed resource genuinely does not exist in any of the three cases. A repeated call
        // lands here too — the row is already gone, and UC-20 has no idempotent path.
        var application = await applicationReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.Id && x.Scope.PublicId == command.ScopeId);

        if (application is null)
        {
            return output.WithError(ApplicationMessages.ApplicationNotFound);
        }

        // UC-20 step 2: remove the record for good. No dependent is deleted first — no entity carries
        // a foreign key to an application, so no foreign key can be violated and there is no total to
        // report, unlike the scope and person hard deletes.
        var delete = await applicationWriter.DeleteAsync(application);

        if (!delete.Success)
        {
            return output.WithErrors(delete.Errors);
        }

        // UC-20 step 3.
        return output
            .WithData(new HardDeleteApplicationCommandOutput { Id = application.PublicId })
            .WithMessage(ApplicationMessages.ApplicationHardDeletedSuccessfully);
    }
}
