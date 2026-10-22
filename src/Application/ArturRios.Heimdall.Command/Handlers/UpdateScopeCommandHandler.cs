using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="UpdateScopeCommand" /> (UC-03): validates the request, verifies the scope
///     exists and is not logically deleted (AF-03a), verifies the new name does not collide with
///     another scope (AF-03b), then applies the changes and stamps <c>UpdatedAt</c>. All failures are
///     returned as errors on the <see cref="DataOutput{T}" /> rather than thrown, using the canonical
///     <see cref="ScopeMessages" /> so the response resolver can pick the matching status code.
/// </summary>
/// <remarks>
///     The changes are one targeted write of the columns this request carries (#205). Google sign-in,
///     self-registration and the email links are left as they stand: they have endpoints of their own
///     (UC-24, FR-SR-02, the email-links one), and this request does not carry them, so it never
///     changes them.
/// </remarks>
public class UpdateScopeCommandHandler(
    IValidator<UpdateScopeCommand> validator,
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IAtomicWrites atomicWrites)
    : ICommandHandlerAsync<UpdateScopeCommand, UpdateScopeCommandOutput>
{
    public async Task<DataOutput<UpdateScopeCommandOutput?>> HandleAsync(UpdateScopeCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<UpdateScopeCommandOutput?>.New;

        // Step 2: validate input fields.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // Step 3 (AF-03a): the scope must exist and not be logically deleted.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.Id && !x.IsDeleted);

        if (scope is null)
        {
            return output.WithError(ScopeMessages.ScopeNotFound);
        }

        // Step 4 (AF-03b): the new name must not collide with another scope, compared
        // case-insensitively (LOWER() in SQL) so the app layer authoritatively rejects case variants
        // that the case-sensitive DB unique index on Name would not catch. Checked against all scopes
        // (deleted included), excluding this scope so an unchanged name is not a false conflict.
        var nameTaken = await scopeReader.Query()
            .AnyAsync(x => x.Name.ToLower() == command.Name.ToLower() && x.PublicId != command.Id);

        if (nameTaken)
        {
            return output.WithError(ScopeMessages.NameAlreadyExists);
        }

        // Owner PublicIds for the response. A projection over the navigation (no Include needed),
        // which EF translates to a join and the in-memory fake evaluates directly.
        var ownerIds = await scopeReader.Query()
            .Where(x => x.PublicId == command.Id)
            .SelectMany(x => x.Owners.Select(owner => owner.Person.PublicId))
            .ToListAsync();

        // Step 4 (main flow): apply the updates and stamp UpdatedAt (no DB trigger maintains it) — those
        // columns only, and only while the scope is not deleted (#205). A whole-row save of the copy read
        // above would write the settings back as read, over a change their own endpoints made meanwhile,
        // and the deletion flag with them.
        var update = await atomicWrites.TryUpdateScopeDetailsAsync(scope, command.Name, command.Description,
            command.DefaultLegalBasis, command.PrivacyNoticeUri, DateTime.UtcNow);

        switch (update)
        {
            // AF-03a: deleted since the read above.
            case ScopeUpdateOutcome.NotFound:
                return output.WithError(ScopeMessages.ScopeNotFound);
            // The unique index on the name, for a name another scope took since the check above, answered
            // as the repository's save answered it.
            case ScopeUpdateOutcome.NameTaken:
                return output.WithError(RelationalErrors.UniqueViolationMessage);
        }

        // Step 5: return the updated scope.
        return output
            .WithData(new UpdateScopeCommandOutput
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
            .WithMessage(ScopeMessages.ScopeUpdatedSuccessfully);
    }
}
