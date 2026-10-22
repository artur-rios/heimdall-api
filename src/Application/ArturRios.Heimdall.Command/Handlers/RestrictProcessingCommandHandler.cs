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
///     Handles <see cref="RestrictProcessingCommand" /> (UC-44): suspends processing of the
///     caller's own identity without deleting anything (GDPR Art. 18, LGPD Art. 18 III–IV). All
///     failures are returned as errors on the <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this is not the deletion flag.</b> `IsDeleted` means the identity is on its way
///         out: it is excluded from reads, it cascades through UC-04, and NFR-20 eventually
///         anonymises it. A restriction means the opposite — the record is disputed and must be
///         preserved exactly as it stands while that is resolved. Reusing the flag would set an
///         erasure clock running on data the subject has specifically asked be kept.
///     </para>
///     <para>
///         <b>No credential is required, unlike UC-42.</b> Erasure is irreversible, so it demands
///         proof the person is present. A restriction destroys nothing and is liftable, and the
///         subject asking for one may be doing so precisely because they believe the account is
///         compromised — demanding the password of somebody in that position would be the wrong way
///         round.
///     </para>
/// </remarks>
public class RestrictProcessingCommandHandler(
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<GoogleUser, long> googleUserReader,
    IAtomicWrites atomicWrites)
    : ICommandHandlerAsync<RestrictProcessingCommand, RestrictProcessingCommandOutput>
{
    public async Task<DataOutput<RestrictProcessingCommandOutput?>> HandleAsync(RestrictProcessingCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<RestrictProcessingCommandOutput?>.New;

        // AF-44b. Art. 18(1) is exhaustive: a restriction rests on one of its four grounds or it is
        // not a restriction under the article.
        if (!Enum.IsDefined((RestrictionGrounds)command.Ground))
        {
            return output.WithError(ErasureMessages.RestrictionGroundUnknown);
        }

        var restrictedAt = DateTime.UtcNow;

        // The lookups omit !IsDeleted deliberately: a suspended identity may still contest the
        // accuracy of what is held about it, and Art. 18 does not require the account to be in good
        // standing. What it must not be is anonymised, and an anonymised record cannot authenticate.
        var person = await personReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ActingPersonId);

        if (person is not null)
        {
            if (person.ProcessingRestrictedAt is not null)
            {
                return output.WithError(ErasureMessages.ProcessingAlreadyRestricted);
            }

            // A targeted write of the restriction's columns alone (#237), and only while there is
            // none: a restriction imposed since the read stands, and this one is refused as the
            // check above would have refused it.
            return await atomicWrites.TryRestrictProcessingAsync(person, command.Ground, restrictedAt)
                ? Restricted(output, person.PublicId, restrictedAt, command.Ground)
                : output.WithError(ErasureMessages.ProcessingAlreadyRestricted);
        }

        var googleUser = await googleUserReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ActingPersonId);

        if (googleUser is null)
        {
            return output.WithError(ErasureMessages.NotEligible);
        }

        if (googleUser.ProcessingRestrictedAt is not null)
        {
            return output.WithError(ErasureMessages.ProcessingAlreadyRestricted);
        }

        // As for a person (#205): the restriction's columns alone, and only while there is none.
        return await atomicWrites.TryRestrictProcessingAsync(googleUser, command.Ground, restrictedAt)
            ? Restricted(output, googleUser.PublicId, restrictedAt, command.Ground)
            : output.WithError(ErasureMessages.ProcessingAlreadyRestricted);
    }

    private static DataOutput<RestrictProcessingCommandOutput?> Restricted(
        DataOutput<RestrictProcessingCommandOutput?> output, Guid id, DateTime at, int ground) =>
        output
            .WithData(new RestrictProcessingCommandOutput { Id = id, RestrictedAt = at, Ground = ground })
            .WithMessage(ErasureMessages.ProcessingRestricted);
}
