using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="RevokeApplicationCredentialCommand" /> (UC-55, FR-AP-12): stamps
///     <c>RevokedAt</c>. From then on UC-56 refuses the secret and UC-57 refuses every token already
///     issued from it — revocation takes effect at the next live re-check, not at token expiry.
/// </summary>
public class RevokeApplicationCredentialCommandHandler(
    IAsyncReadOnlyRepository<Application, long> applicationReader,
    IAsyncReadOnlyRepository<ApplicationCredential, long> credentialReader,
    IAsyncRepository<ApplicationCredential, long> credentialWriter)
    : ICommandHandlerAsync<RevokeApplicationCredentialCommand, RevokeApplicationCredentialCommandOutput>
{
    public async Task<DataOutput<RevokeApplicationCredentialCommandOutput?>> HandleAsync(
        RevokeApplicationCredentialCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<RevokeApplicationCredentialCommandOutput?>.New;

        // AF-55a, then AF-55b.
        var access = await ApplicationManagementAccess.ResolveAsync(
            applicationReader.Query(), command.ScopeId, command.Id, command.ActingRole, command.ActingPersonId);

        switch (access.Access)
        {
            case ApplicationAccess.NotFound:
                return output.WithError(ApplicationMessages.ApplicationNotFound);
            case ApplicationAccess.Forbidden:
                return output.WithError(ApplicationCredentialMessages.NotAuthorizedToManageCredentials);
        }

        // AF-55c: the credential must belong to this application; one of another is not the
        // resource this route addresses.
        var applicationId = access.Application!.Id;
        var credential = await credentialReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.CredentialId && x.ApplicationId == applicationId,
                cancellationToken);

        if (credential is null)
        {
            return output.WithError(ApplicationCredentialMessages.CredentialNotFound);
        }

        // AF-55d: the first revocation's time stands.
        if (credential.RevokedAt is not null)
        {
            return output.WithError(ApplicationCredentialMessages.CredentialAlreadyRevoked);
        }

        credential.RevokedAt = DateTime.UtcNow;

        var update = await credentialWriter.UpdateAsync(credential);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        return output
            .WithData(new RevokeApplicationCredentialCommandOutput
            {
                Id = credential.PublicId,
                RevokedAt = credential.RevokedAt.Value
            })
            .WithMessage(ApplicationCredentialMessages.CredentialRevoked);
    }
}
