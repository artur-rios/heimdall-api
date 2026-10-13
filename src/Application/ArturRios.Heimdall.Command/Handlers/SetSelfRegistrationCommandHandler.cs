using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="SetSelfRegistrationCommand" /> (UC-47 toggle, FR-SR-02): the same order of
///     checks as <see cref="SetGoogleSignInCommandHandler" /> — the value is stated, the scope is live,
///     a Scope Admin actor owns it — then writes <c>SelfRegistrationEnabled</c> and returns the scope.
/// </summary>
public class SetSelfRegistrationCommandHandler(
    IValidator<SetSelfRegistrationCommand> validator,
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IAsyncRepository<Scope, long> scopeWriter,
    IScopeOwnershipChecker scopeOwnership)
    : ICommandHandlerAsync<SetSelfRegistrationCommand, SetSelfRegistrationCommandOutput>
{
    public async Task<DataOutput<SetSelfRegistrationCommandOutput?>> HandleAsync(
        SetSelfRegistrationCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<SetSelfRegistrationCommandOutput?>.New;

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

        scope.SelfRegistrationEnabled = command.Enabled!.Value;
        scope.UpdatedAt = DateTime.UtcNow;

        var update = await scopeWriter.UpdateAsync(scope);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        return output
            .WithData(new SetSelfRegistrationCommandOutput
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
            .WithMessage(ScopeMessages.SelfRegistrationUpdatedSuccessfully);
    }
}
