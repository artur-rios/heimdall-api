using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="LiftProcessingRestrictionCommand" /> (UC-45): lifts a restriction, after
///     informing the subject where the law requires it. All failures are returned as errors on the
///     <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
/// <remarks>
///     <para>
///         <b>Art. 18(3) is a precondition, not a courtesy.</b> The subject "shall be informed
///         before the restriction is lifted", so a failed notification stops the lift. That is the
///         opposite of how every other delivery in this API behaves — elsewhere a failure is
///         deliberately not the caller's problem — and the difference is deliberate: a lift that
///         proceeded after a failed send would be unlawful and would look identical to one that
///         worked.
///     </para>
///     <para>
///         <b>A subject lifting their own restriction needs no notification.</b> They are the person
///         Art. 18(3) exists to inform. Requiring an email to be delivered to somebody who is
///         standing in front of you, and refusing their request when it bounces, would be the
///         article's letter against its purpose.
///     </para>
/// </remarks>
public class LiftProcessingRestrictionCommandHandler(
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Person> personWriter,
    IAsyncReadOnlyRepository<GoogleUser> googleUserReader,
    IAsyncRepository<GoogleUser> googleUserWriter,
    IRestrictionLiftNotifier notifier)
    : ICommandHandlerAsync<LiftProcessingRestrictionCommand, LiftProcessingRestrictionCommandOutput>
{
    public async Task<DataOutput<LiftProcessingRestrictionCommandOutput?>> HandleAsync(
        LiftProcessingRestrictionCommand command)
    {
        var output = DataOutput<LiftProcessingRestrictionCommandOutput?>.New;

        var subjectId = command.SubjectId ?? command.ActingPersonId;
        var liftingOwn = subjectId == command.ActingPersonId;

        // Only a System Admin may lift somebody else's. The endpoint carries no role requirement,
        // because a subject of any role may lift their own, so the rule is data-dependent and lives
        // here.
        if (!liftingOwn && command.ActingRole != (int)Roles.SystemAdmin)
        {
            return output.WithError(ErasureMessages.NotEligible);
        }

        var person = await personReader.Query().FirstOrDefaultAsync(x => x.PublicId == subjectId);

        if (person is not null)
        {
            return await LiftAsync(
                output, person.ProcessingRestrictedAt, person.Email, liftingOwn,
                notifiedAt =>
                {
                    person.ProcessingRestrictedAt = null;
                    person.RestrictionGround = null;
                    person.RestrictionLiftNotifiedAt = notifiedAt;
                    person.UpdatedAt = DateTime.UtcNow;
                },
                () => personWriter.UpdateAsync(person),
                person.PublicId);
        }

        var googleUser = await googleUserReader.Query().FirstOrDefaultAsync(x => x.PublicId == subjectId);

        if (googleUser is null)
        {
            return output.WithError(ErasureMessages.NotEligible);
        }

        return await LiftAsync(
            output, googleUser.ProcessingRestrictedAt, googleUser.Email, liftingOwn,
            notifiedAt =>
            {
                googleUser.ProcessingRestrictedAt = null;
                googleUser.RestrictionGround = null;
                googleUser.RestrictionLiftNotifiedAt = notifiedAt;
                googleUser.UpdatedAt = DateTime.UtcNow;
            },
            () => googleUserWriter.UpdateAsync(googleUser),
            googleUser.PublicId);
    }

    private async Task<DataOutput<LiftProcessingRestrictionCommandOutput?>> LiftAsync<T>(
        DataOutput<LiftProcessingRestrictionCommandOutput?> output,
        DateTime? restrictedAt,
        string email,
        bool liftingOwn,
        Action<DateTime?> clear,
        Func<Task<DataOutput<T>>> save,
        Guid subjectPublicId)
    {
        // AF-45a.
        if (restrictedAt is null)
        {
            return output.WithError(ErasureMessages.NotRestricted);
        }

        DateTime? notifiedAt = null;

        if (!liftingOwn)
        {
            // AF-45b. Informing comes first and the lift does not proceed without it.
            if (!await notifier.NotifyAsync(email))
            {
                return output.WithError(ErasureMessages.RestrictionLiftNotificationFailed);
            }

            notifiedAt = DateTime.UtcNow;
        }

        clear(notifiedAt);

        var update = await save();

        return update.Success
            ? output
                .WithData(new LiftProcessingRestrictionCommandOutput
                {
                    Id = subjectPublicId,
                    LiftedAt = DateTime.UtcNow,
                    SubjectNotified = notifiedAt is not null
                })
                .WithMessage(ErasureMessages.RestrictionLifted)
            : output.WithErrors(update.Errors);
    }
}
