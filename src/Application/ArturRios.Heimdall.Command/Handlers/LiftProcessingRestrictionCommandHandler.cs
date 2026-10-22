using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
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
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<GoogleUser, long> googleUserReader,
    IRestrictionLiftNotifier notifier,
    IAtomicWrites atomicWrites)
    : ICommandHandlerAsync<LiftProcessingRestrictionCommand, LiftProcessingRestrictionCommandOutput>
{
    public async Task<DataOutput<LiftProcessingRestrictionCommandOutput?>> HandleAsync(
        LiftProcessingRestrictionCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<LiftProcessingRestrictionCommandOutput?>.New;

        var subjectId = command.SubjectId ?? command.ActingPersonId;
        var liftingOwn = subjectId == command.ActingPersonId;

        // Only a System Admin may lift somebody else's. The endpoint carries no role requirement,
        // because a subject of any role may lift their own, so the rule is data-dependent and lives
        // here.
        //
        // A subject-rights-only token acts as a subject and never as an administrator (AF-45c),
        // including after its own restriction was lifted: it was issued to a restricted identity,
        // and that is all it ever stands for.
        if (!liftingOwn && (command.ActingRole != (int)Roles.SystemAdmin || command.ActingSubjectRightsOnly))
        {
            return output.WithError(ErasureMessages.NotEligible);
        }

        // The endpoint admits a restricted or suspended caller, because lifting their own restriction
        // is a right of theirs (NFR-24). Lifting somebody else's is not: an administrator who is
        // restricted or suspended themselves acts here as a subject, not as an administrator.
        if (!liftingOwn && await personReader.Query().AnyAsync(x =>
                x.PublicId == command.ActingPersonId && (x.IsDeleted || x.ProcessingRestrictedAt != null)))
        {
            return output.WithError(ErasureMessages.NotEligible);
        }

        var person = await personReader.Query().FirstOrDefaultAsync(x => x.PublicId == subjectId);

        if (person is not null)
        {
            // A targeted write of the restriction's columns alone (#237), and only of the
            // restriction read: one lifted since answers AF-45a, as it would have a moment earlier,
            // and one imposed again since is not lifted by a notice sent about the first.
            return await LiftAsync(
                output, person.ProcessingRestrictedAt, person.Email, liftingOwn,
                async notifiedAt => await atomicWrites.TryLiftRestrictionAsync(person, notifiedAt, DateTime.UtcNow)
                    ? null
                    : [ErasureMessages.NotRestricted],
                person.PublicId);
        }

        var googleUser = await googleUserReader.Query().FirstOrDefaultAsync(x => x.PublicId == subjectId);

        if (googleUser is null)
        {
            return output.WithError(ErasureMessages.NotEligible);
        }

        // As for a person (#205): the restriction's columns alone, and only of the restriction read.
        return await LiftAsync(
            output, googleUser.ProcessingRestrictedAt, googleUser.Email, liftingOwn,
            async notifiedAt => await atomicWrites.TryLiftRestrictionAsync(googleUser, notifiedAt, DateTime.UtcNow)
                ? null
                : [ErasureMessages.NotRestricted],
            googleUser.PublicId);
    }

    /// <param name="output">The output to answer on.</param>
    /// <param name="restrictedAt">When the restriction read began, or <c>null</c> when there is none.</param>
    /// <param name="email">Where the Art. 18(3) notice goes.</param>
    /// <param name="liftingOwn">Whether the subject is lifting their own restriction.</param>
    /// <param name="lift">
    ///     Lifts and persists, given when the subject was told; returns the errors, or <c>null</c>.
    /// </param>
    /// <param name="subjectPublicId">The subject's public id, for the response.</param>
    private async Task<DataOutput<LiftProcessingRestrictionCommandOutput?>> LiftAsync(
        DataOutput<LiftProcessingRestrictionCommandOutput?> output,
        DateTime? restrictedAt,
        string email,
        bool liftingOwn,
        Func<DateTime?, Task<IEnumerable<string>?>> lift,
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

        var errors = await lift(notifiedAt);

        return errors is null
            ? output
                .WithData(new LiftProcessingRestrictionCommandOutput
                {
                    Id = subjectPublicId,
                    LiftedAt = DateTime.UtcNow,
                    SubjectNotified = notifiedAt is not null
                })
                .WithMessage(ErasureMessages.RestrictionLifted)
            : output.WithErrors(errors);
    }
}
