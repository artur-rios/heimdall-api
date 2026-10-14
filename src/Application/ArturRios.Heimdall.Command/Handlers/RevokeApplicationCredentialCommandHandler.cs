using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
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
/// <remarks>
///     The stamp is one conditional column (<see cref="IAtomicWrites.TryRevokeCredentialAsync" />), not
///     a whole-row save: of two revocations that both read the credential live, only the first writes,
///     and the second answers AF-55d rather than overwriting the first one's time.
/// </remarks>
public class RevokeApplicationCredentialCommandHandler(
    IAsyncReadOnlyRepository<Application, long> applicationReader,
    IAsyncReadOnlyRepository<ApplicationCredential, long> credentialReader,
    IAtomicWrites atomicWrites)
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

        // AF-55d: the first revocation's time stands — decided by the write, not by what was read,
        // so a revocation committed after the read above is not overwritten.
        var revokedAt = DateTime.UtcNow;

        if (!await atomicWrites.TryRevokeCredentialAsync(credential, revokedAt))
        {
            return output.WithError(ApplicationCredentialMessages.CredentialAlreadyRevoked);
        }

        return output
            .WithData(new RevokeApplicationCredentialCommandOutput
            {
                Id = credential.PublicId,
                RevokedAt = revokedAt
            })
            .WithMessage(ApplicationCredentialMessages.CredentialRevoked);
    }
}
