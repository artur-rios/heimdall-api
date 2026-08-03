using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Command.Handlers;

/// <summary>
///     Handles <see cref="DeleteGoogleUserCommand" /> (UC-28, FR-GO-15): locates the Google User
///     inside the addressed scope (AF-28a), enforces the scope-ownership rule (AF-28c), serves an
///     already-deleted record as an idempotent no-op (AF-28b), and otherwise sets
///     <c>IsDeleted = true</c> and stamps <c>UpdatedAt</c>. Nothing cascades — see the remarks. All
///     failures are returned as errors on the <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
/// <remarks>
///     <para>
///         The shape of <see cref="DeleteApplicationCommandHandler" />, differing in step 2 only.
///         UC-19 compares the application's <em>owner</em>, because owning a scope is not grounds to
///         delete another owner's application; a Google User has no owner, only a scope (FR-GO-06),
///         and UC-28 names "a System Admin, or an owner of the Google User's scope" — which is
///         exactly what <see cref="IScopeOwnershipChecker" /> decides.
///     </para>
///     <para>
///         Nothing cascades because there is nothing to cascade to. A Google User can own no
///         application — FR-AP-03 restricts that to a <c>ScopeAdmin</c> who owns the scope, and a
///         Google User is always <c>User</c>-equivalent (FR-GO-04) — and it holds no password reset
///         or email verification tokens, since authentication is delegated to Google.
///     </para>
///     <para>
///         The flag this sets is already honoured everywhere it matters: UC-25 refuses to
///         authenticate a deleted Google User (AF-25d), UC-26 refuses their sign-out (AF-26a), and
///         UC-27 hides them from default reads (FR-GO-17).
///     </para>
/// </remarks>
public class DeleteGoogleUserCommandHandler(
    IAsyncReadOnlyRepository<GoogleUser> googleUserReader,
    IAsyncRepository<GoogleUser> googleUserWriter,
    IScopeOwnershipChecker scopeOwnership)
    : ICommandHandlerAsync<DeleteGoogleUserCommand, DeleteGoogleUserCommandOutput>
{
    public async Task<DataOutput<DeleteGoogleUserCommandOutput?>> HandleAsync(DeleteGoogleUserCommand command)
    {
        var output = DataOutput<DeleteGoogleUserCommandOutput?>.New;

        // AF-28a: the Google User must exist inside the addressed scope. The lookup deliberately omits
        // the !IsDeleted filter UC-27's default read applies, so an already-deleted record is found
        // and served idempotently by AF-28b below rather than reported as not found — without that,
        // AF-28b could never fire. The route's scopeId qualifies it: a Google User that belongs to
        // another scope is not the resource this path addresses.
        var googleUser = await googleUserReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.Id && x.Scope.PublicId == command.ScopeId);

        if (googleUser is null)
        {
            return output.WithError(GoogleUserMessages.GoogleUserNotFound);
        }

        // AF-28c (UC-28 step 2): a System Admin may delete any Google User; a Scope Admin only those
        // of the scopes they own — which is what the checker decides, along with its guard that a
        // logically deleted actor owns nothing. Runs before AF-28b so an already-deleted Google User
        // cannot be used to probe for records outside the caller's reach.
        if (!await scopeOwnership.ActorMayManageScopeAsync(
                command.ActingRole, command.ActingPersonId, googleUser.ScopeId))
        {
            return output.WithError(GoogleUserMessages.NotAuthorizedToDeleteGoogleUser);
        }

        // AF-28b: already deleted — whether directly or by UC-04's cascade from its scope — so there
        // is nothing to write. UpdatedAt is left alone: the row already carries the requested state,
        // and re-stamping it would misreport when the deletion happened.
        if (googleUser.IsDeleted)
        {
            return output
                .WithData(new DeleteGoogleUserCommandOutput { Id = googleUser.PublicId, AlreadyDeleted = true })
                .WithMessage(GoogleUserMessages.GoogleUserDeletedSuccessfully);
        }

        // UC-28 step 3: flip the flag and stamp UpdatedAt (no DB trigger maintains it).
        googleUser.IsDeleted = true;
        googleUser.UpdatedAt = DateTime.UtcNow;

        var update = await googleUserWriter.UpdateAsync(googleUser);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        // UC-28 step 4.
        return output
            .WithData(new DeleteGoogleUserCommandOutput { Id = googleUser.PublicId, AlreadyDeleted = false })
            .WithMessage(GoogleUserMessages.GoogleUserDeletedSuccessfully);
    }
}
