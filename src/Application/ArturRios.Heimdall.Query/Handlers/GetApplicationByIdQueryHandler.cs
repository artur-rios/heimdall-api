using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>
///     Handles <see cref="GetApplicationByIdQuery" /> (UC-17, FR-AP-04): retrieves an application by
///     its <c>PublicId</c> within the addressed scope, excluding logically deleted applications
///     unless explicitly requested (FR-AP-09), then applies the use case's visibility rule — a System
///     Admin sees any application, a Scope Admin only the ones they own. A miss is AF-17a
///     (<c>ApplicationNotFound</c>); an application the caller may not see is AF-17b
///     (<c>NotAuthorizedToViewApplication</c>). Both are returned as errors rather than thrown.
/// </summary>
public class GetApplicationByIdQueryHandler(IAsyncReadOnlyRepository<Application> applicationReader)
    : IQueryHandlerAsync<GetApplicationByIdQuery, ApplicationOutput>
{
    public async Task<DataOutput<ApplicationOutput?>> HandleAsync(GetApplicationByIdQuery query)
    {
        var output = DataOutput<ApplicationOutput?>.New;

        // The route's scopeId qualifies the lookup: an application that exists in another scope is
        // not the resource this path addresses, so it falls out here rather than at the rule below.
        var application = await applicationReader.Query()
            .Where(x => x.PublicId == query.Id && x.Scope.PublicId == query.ScopeId &&
                        (query.IncludeDeleted || !x.IsDeleted))
            .Select(x => new ApplicationOutput
            {
                Id = x.PublicId,
                Name = x.Name,
                ScopeId = x.Scope.PublicId,
                OwnerId = x.Owner.PublicId,
                IsDeleted = x.IsDeleted,
                CreatedAt = x.CreatedAt,
                UpdatedAt = x.UpdatedAt
            })
            .FirstOrDefaultAsync();

        // AF-17a: no such application under this scope (or it is logically deleted and was not
        // explicitly requested). Checked before authorization, so both alternative flows stay
        // observable — a GUID nobody holds cannot be told apart from one the caller may not see.
        if (application is null)
        {
            return output.WithError(ApplicationMessages.ApplicationNotFound);
        }

        // AF-17b (UC-17 step 2): a System Admin sees every application; anyone else must own it.
        // Owning the *scope* is not by itself grounds to read another owner's application, so the
        // rule compares the owner rather than consulting IScopeOwnershipChecker.
        if (query.ActingRole != (int)Roles.SystemAdmin && application.OwnerId != query.ActingPersonId)
        {
            return output.WithError(ApplicationMessages.NotAuthorizedToViewApplication);
        }

        return output
            .WithData(application)
            .WithMessage(ApplicationMessages.ApplicationRetrievedSuccessfully);
    }
}
