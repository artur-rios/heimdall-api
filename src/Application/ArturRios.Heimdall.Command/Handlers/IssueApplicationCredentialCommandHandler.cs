using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Heimdall.Shared.Time;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="IssueApplicationCredentialCommand" /> (UC-53, FR-AP-10/11/12): generates a
///     secret, stores its hash, and returns the secret once.
/// </summary>
/// <remarks>
///     The limit of <see cref="ApplicationCredential.MaxLive" /> is checked and the row written by
///     <see cref="IApplicationCredentialSlots" /> in one locked step; checking here and inserting
///     through a repository would let two concurrent requests both pass.
/// </remarks>
public class IssueApplicationCredentialCommandHandler(
    IValidator<IssueApplicationCredentialCommand> validator,
    IAsyncReadOnlyRepository<Application, long> applicationReader,
    IApplicationCredentialSlots slots)
    : ICommandHandlerAsync<IssueApplicationCredentialCommand, IssueApplicationCredentialCommandOutput>
{
    public async Task<DataOutput<IssueApplicationCredentialCommandOutput?>> HandleAsync(
        IssueApplicationCredentialCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<IssueApplicationCredentialCommandOutput?>.New;

        // AF-53c.
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-53a, then AF-53b.
        var access = await ApplicationManagementAccess.ResolveAsync(
            applicationReader.Query(), command.ScopeId, command.Id, command.ActingRole, command.ActingPersonId);

        switch (access.Access)
        {
            case ApplicationAccess.NotFound:
                return output.WithError(ApplicationMessages.ApplicationNotFound);
            case ApplicationAccess.Forbidden:
                return output.WithError(ApplicationCredentialMessages.NotAuthorizedToManageCredentials);
        }

        var now = DateTime.UtcNow;
        var secret = ApplicationCredentialSecret.New();
        var credential = new ApplicationCredential
        {
            ApplicationId = access.Application!.Id,
            SecretHash = ApplicationCredentialSecret.HashOf(secret),
            CreatedAt = now,
            ExpiresAt = command.ExpiresAt is { } expiresAt ? UtcInstant.Of(expiresAt) : null
        };

        // AF-53d.
        if (!await slots.TryAddWithinLimitAsync(credential, ApplicationCredential.MaxLive, now))
        {
            return output.WithError(ApplicationCredentialMessages.CredentialLimitReached);
        }

        return output
            .WithData(new IssueApplicationCredentialCommandOutput
            {
                Id = credential.PublicId,
                Secret = secret,
                CreatedAt = credential.CreatedAt,
                ExpiresAt = credential.ExpiresAt
            })
            .WithMessage(ApplicationCredentialMessages.CredentialIssued);
    }
}
