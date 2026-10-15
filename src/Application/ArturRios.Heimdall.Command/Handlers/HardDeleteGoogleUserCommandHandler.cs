using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="HardDeleteGoogleUserCommand" /> (UC-29, FR-GO-16): locates the Google User
///     inside the addressed scope in any deletion state (AF-29a) and permanently removes the record.
///     Nothing cascades — the Deletion Strategy says a hard delete here "simply removes its record",
///     and the scope its foreign key points at is left intact. Authorization is entirely the
///     endpoint's: UC-29's only actor is the System Admin, so no rule is left for the handler to
///     apply. All failures are returned as errors on the <see cref="DataOutput{T}" /> rather than
///     thrown.
/// </summary>
/// <remarks>
///     <para>
///         No self-deletion refusal, unlike <see cref="DeletePersonCommandHandler" /> (AF-09d) and
///         <see cref="HardDeletePersonCommandHandler" /> (AF-10c). A Google User is always
///         <c>User</c>-equivalent (FR-GO-04) and can never hold <c>SystemAdmin</c>, so the only actor who
///         may call this endpoint can never be its target — there is no way to lock yourself out, and a
///         guard against it would be a flow no document defines.
///     </para>
///     <para>
///         Before the record is removed, its <c>PublicId</c> is tombstoned (<see cref="ErasedSubject" />,
///         NFR-21, #195), so the audit entries that name it as their target — this hard delete's own
///         among them — can still be cleared once nothing else says the id was a person's. Written
///         first, a failed delete leaves a tombstone for a live row, which nothing acts on; and a retry
///         writes it again without error.
///     </para>
/// </remarks>
public class HardDeleteGoogleUserCommandHandler(
    IAsyncReadOnlyRepository<GoogleUser, long> googleUserReader,
    IAsyncRepository<GoogleUser, long> googleUserWriter,
    IErasedSubjects erasedSubjects)
    : ICommandHandlerAsync<HardDeleteGoogleUserCommand, HardDeleteGoogleUserCommandOutput>
{
    public async Task<DataOutput<HardDeleteGoogleUserCommandOutput?>> HandleAsync(
        HardDeleteGoogleUserCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<HardDeleteGoogleUserCommandOutput?>.New;

        // AF-29a: the Google User must exist inside the addressed scope. The lookup omits an
        // !IsDeleted filter — a logically deleted Google User, whether by UC-28 or by UC-04's cascade
        // from its scope, is exactly what a cleanup pass starts from and must still be purgeable, the
        // same call UC-05, UC-10, and UC-20 make. The route's scopeId qualifies it: an unknown Google
        // User, an unknown scope, and a Google User living in another scope are all one 404, because
        // the addressed resource genuinely does not exist in any of the three cases. A repeated call
        // lands here too — the row is already gone, and UC-29 has no idempotent path as UC-28 does.
        var googleUser = await googleUserReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.Id && x.Scope.PublicId == command.ScopeId);

        if (googleUser is null)
        {
            return output.WithError(GoogleUserMessages.GoogleUserNotFound);
        }

        // UC-29 step 2 (NFR-21, #195): the tombstone goes first, so the row is never gone without it.
        await erasedSubjects.RecordAsync(
        [
            new ErasedSubject
            {
                Id = googleUser.PublicId,
                Kind = (int)ErasedSubjectKinds.GoogleUser,
                CreatedAt = DateTime.UtcNow
            }
        ]);

        // UC-29 step 3: remove the record for good. No dependent is deleted first — a Google User can
        // own no application (FR-AP-03) and holds no password reset or email verification tokens,
        // since authentication is delegated to Google — so no foreign key can be violated and there
        // is no total to report, unlike the scope and person hard deletes.
        var delete = await googleUserWriter.DeleteAsync(googleUser);

        if (!delete.Success)
        {
            return output.WithErrors(delete.Errors);
        }

        // UC-29 step 4.
        return output
            .WithData(new HardDeleteGoogleUserCommandOutput { Id = googleUser.PublicId })
            .WithMessage(GoogleUserMessages.GoogleUserHardDeletedSuccessfully);
    }
}
