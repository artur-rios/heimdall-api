using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>Handles <see cref="ListApplicationCredentialsQuery" /> (UC-54, FR-AP-12).</summary>
public class ListApplicationCredentialsQueryHandler(
    IAsyncReadOnlyRepository<Application, long> applicationReader,
    IAsyncReadOnlyRepository<ApplicationCredential, long> credentialReader)
    : IQueryHandlerAsync<ListApplicationCredentialsQuery, ApplicationCredentialListOutput>
{
    public async Task<DataOutput<ApplicationCredentialListOutput?>> HandleAsync(
        ListApplicationCredentialsQuery query, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<ApplicationCredentialListOutput?>.New;

        // AF-54a, then AF-54b.
        var access = await ApplicationManagementAccess.ResolveAsync(
            applicationReader.Query(), query.ScopeId, query.Id, query.ActingRole, query.ActingPersonId);

        switch (access.Access)
        {
            case ApplicationAccess.NotFound:
                return output.WithError(ApplicationMessages.ApplicationNotFound);
            case ApplicationAccess.Forbidden:
                return output.WithError(ApplicationCredentialMessages.NotAuthorizedToManageCredentials);
        }

        var applicationId = access.Application!.Id;
        var credentials = await credentialReader.Query()
            .Where(x => x.ApplicationId == applicationId)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        // Liveness is judged once, in memory, so every row is measured against the same instant.
        var now = DateTime.UtcNow;

        return output
            .WithData(new ApplicationCredentialListOutput
            {
                Credentials = credentials.Select(x => new ApplicationCredentialOutput
                {
                    Id = x.PublicId,
                    CreatedAt = x.CreatedAt,
                    ExpiresAt = x.ExpiresAt,
                    RevokedAt = x.RevokedAt,
                    LastUsedAt = x.LastUsedAt,
                    Live = x.IsLiveAt(now)
                }).ToList()
            })
            .WithMessage(ApplicationCredentialMessages.CredentialsRetrieved);
    }
}
