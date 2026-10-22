using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="SetScopeEmailLinksCommand" />: the same order of checks as
///     <see cref="SetSelfRegistrationCommandHandler" /> — the links are usable, the scope is live, a
///     Scope Admin actor owns it — then writes <c>EmailVerificationUrl</c> and <c>PasswordResetUrl</c>
///     and returns the scope. Nothing else on the scope is touched.
/// </summary>
public class SetScopeEmailLinksCommandHandler(
    IValidator<SetScopeEmailLinksCommand> validator,
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IAtomicWrites atomicWrites,
    IScopeOwnershipChecker scopeOwnership)
    : ICommandHandlerAsync<SetScopeEmailLinksCommand, SetScopeEmailLinksCommandOutput>
{
    public async Task<DataOutput<SetScopeEmailLinksCommandOutput?>> HandleAsync(
        SetScopeEmailLinksCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<SetScopeEmailLinksCommandOutput?>.New;

        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.Id && !x.IsDeleted, cancellationToken);

        if (scope is null)
        {
            return output.WithError(ScopeMessages.ScopeNotFound);
        }

        if (!await scopeOwnership.ActorMayManageScopeAsync(command.ActingRole, command.ActingPersonId, scope.Id))
        {
            return output.WithError(ScopeMessages.NotScopeOwner);
        }

        var ownerIds = await scopeReader.Query()
            .Where(x => x.PublicId == command.Id)
            .SelectMany(x => x.Owners.Select(owner => owner.Person.PublicId))
            .ToListAsync(cancellationToken);

        // The two links alone, and only while the scope is not deleted (#205), as UC-24 writes its flag:
        // one deleted since the read above is answered as one deleted before it.
        if (!await atomicWrites.TrySetEmailLinksAsync(scope, Normalised(command.EmailVerificationUrl),
                Normalised(command.PasswordResetUrl), DateTime.UtcNow))
        {
            return output.WithError(ScopeMessages.ScopeNotFound);
        }

        return output
            .WithData(new SetScopeEmailLinksCommandOutput
            {
                Id = scope.PublicId,
                Name = scope.Name,
                Description = scope.Description,
                GoogleSignInEnabled = scope.GoogleSignInEnabled,
                SelfRegistrationEnabled = scope.SelfRegistrationEnabled,
                EmailVerificationUrl = scope.EmailVerificationUrl,
                PasswordResetUrl = scope.PasswordResetUrl,
                OwnerIds = ownerIds,
                CreatedAt = scope.CreatedAt,
                UpdatedAt = scope.UpdatedAt
            })
            .WithMessage(ScopeMessages.EmailLinksUpdatedSuccessfully);
    }

    // An empty or blank link is stored as "no link", so the senders have one way to read it.
    private static string? Normalised(string? url) => string.IsNullOrWhiteSpace(url) ? null : url.Trim();
}
