using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="IssueApplicationTokenCommand" /> (UC-56, FR-AP-13/14): finds the live
///     credential by the hash of the presented secret, checks it belongs to the named application in
///     the named scope, both live, and issues a token.
/// </summary>
/// <remarks>
///     <para>
///         <b>One refusal.</b> An unknown secret, a revoked or expired one, another application's, a
///         deleted application or scope, and the wrong scope all answer
///         <see cref="ApplicationTokenMessages.CredentialsNotAccepted" />, so the endpoint says
///         nothing about which applications, scopes or credentials exist (the stance UC-11 takes for
///         persons). The audit entry of a refusal therefore has no target.
///     </para>
///     <para>
///         <b>Whitespace.</b> A secret has none, so it is trimmed: one read from a file arrives with
///         its trailing newline, and refusing it would be a failure nobody could diagnose from one
///         uniform 401.
///     </para>
///     <para>
///         <b>Stamped before signing.</b> <c>LastUsedAt</c> is written as one conditional column
///         (<see cref="IAtomicWrites.TryStampCredentialUseAsync" />) that only lands while the
///         credential is still live, and the token is signed only after it has. A revocation (UC-55)
///         that commits between the read and the stamp therefore refuses this exchange, rather than
///         being written back over by a whole-row save of the credential as it was read.
///     </para>
/// </remarks>
public class IssueApplicationTokenCommandHandler(
    IValidator<IssueApplicationTokenCommand> validator,
    IAsyncReadOnlyRepository<ApplicationCredential, long> credentialReader,
    IAtomicWrites atomicWrites,
    IApplicationTokenIssuer issuer)
    : ICommandHandlerAsync<IssueApplicationTokenCommand, IssueApplicationTokenCommandOutput>
{
    public async Task<DataOutput<IssueApplicationTokenCommandOutput?>> HandleAsync(
        IssueApplicationTokenCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<IssueApplicationTokenCommandOutput?>.New;

        // AF-56a.
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        var now = DateTime.UtcNow;
        var hash = ApplicationCredentialSecret.HashOf(command.Secret.Trim());

        var credential = await credentialReader.Query()
            .Include(x => x.Application)
            .ThenInclude(application => application.Scope)
            .Where(ApplicationCredential.LiveAt(now))
            .FirstOrDefaultAsync(x => x.SecretHash == hash, cancellationToken);

        // AF-56b — every check, one answer.
        if (credential is null ||
            credential.Application.PublicId != command.ApplicationId ||
            credential.Application.IsDeleted ||
            credential.Application.Scope.PublicId != command.ScopeId ||
            credential.Application.Scope.IsDeleted)
        {
            return output.WithError(ApplicationTokenMessages.CredentialsNotAccepted);
        }

        // AF-56b again: revoked or expired since the read above.
        if (!await atomicWrites.TryStampCredentialUseAsync(credential, now))
        {
            return output.WithError(ApplicationTokenMessages.CredentialsNotAccepted);
        }

        var application = credential.Application;
        var token = await issuer.IssueAsync(new ApplicationTokenSubject(
            application.PublicId, application.Scope.PublicId, credential.PublicId, application.Name));

        return output
            .WithData(new IssueApplicationTokenCommandOutput
            {
                Id = application.PublicId,
                Token = token.Token,
                ExpiresAt = token.ExpiresAt
            })
            .WithMessage(ApplicationTokenMessages.TokenIssued);
    }
}
