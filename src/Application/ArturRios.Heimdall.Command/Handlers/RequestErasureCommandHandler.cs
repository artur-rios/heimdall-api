using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="RequestErasureCommand" /> (UC-42): re-authenticates the caller, records
///     their erasure request against their own record, lifts any restriction on processing they
///     had in place, and suspends the identity so the request takes effect immediately. The
///     anonymisation pass (NFR-20) completes it on the deadline. All failures are returned as
///     errors on the <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why the subject is never named in the request.</b> The identity acted on is the one
///         the token names, always. There is no path parameter and no body field for it, so the
///         endpoint has no shape in which one identity could ask for another's erasure — a rule
///         enforced by the absence of the parameter rather than by a check that could be got wrong.
///     </para>
///     <para>
///         <b>Why re-authentication.</b> Erasure is irreversible, and a bearer token is not proof
///         that the person is present — one left open on a shared machine, or lifted from a log,
///         would otherwise be enough to destroy the account it belongs to. A person presents their
///         password; a Google User presents a fresh Google ID token, because they have no password.
///         A person's password is charged to their re-authentication budget (FR-AU-13) and refused
///         with the message every re-authentication shares; a Google User's token, which cannot be
///         guessed, is refused with <see cref="ErasureMessages.CredentialNotAccepted" />. Both are 401.
///     </para>
///     <para>
///         <b>Why the request is recorded before it can be carried out.</b> NFR-12 refuses to
///         suspend the last owner of a scope, and clearing that needs a human to transfer ownership
///         (UC-21). Refusing the request outright would be the wrong answer twice over: the subject
///         has exercised a right that does not depend on the scope's ownership arrangements, and
///         GDPR Art. 12(3)'s clock starts at the request whether or not anything blocks it. So the
///         request is always recorded and always starts the deadline; the block is recorded
///         alongside it, and the anonymisation pass carries the erasure out as soon as it clears.
///     </para>
///     <para>
///         <b>Why the request lifts a restriction.</b> A restricted record is kept out of the
///         anonymisation pass (NFR-24), so an erasure requested under a restriction would wait,
///         unerased, for as long as the restriction stood — and the statutory deadline would pass
///         meanwhile. Only the subject can impose a restriction (UC-44), and only the subject can
///         get here, credential in hand: asking to be erased is their later and more specific
///         instruction, and it supersedes their wish that the record be preserved. The lift is the
///         one UC-45 performs for a subject lifting their own — no Art. 18(3) notice, since they
///         are the person it would inform — and it is saved with the request, so neither can
///         happen without the other. The response and the audit trail both say it happened.
///     </para>
///     <para>
///         <b>Why the deadline is stored rather than derived.</b> The obligation attaches when the
///         request is made. Recomputing it from the current configuration on every read would let a
///         later change move an obligation that had already attached.
///     </para>
/// </remarks>
public class RequestErasureCommandHandler(
    IValidator<RequestErasureCommand> validator,
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<GoogleUser, long> googleUserReader,
    IAsyncRepository<GoogleUser, long> googleUserWriter,
    IGoogleIdTokenVerifier tokenVerifier,
    DataRetentionOptions retention,
    IAtomicWrites atomicWrites)
    : ICommandHandlerAsync<RequestErasureCommand, RequestErasureCommandOutput>
{
    public async Task<DataOutput<RequestErasureCommandOutput?>> HandleAsync(RequestErasureCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<RequestErasureCommandOutput?>.New;

        // NFR-10: validate input shape.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // The person table first, then the Google User table — the same order UC-36 uses, and for
        // the same reason: the two identity tables are never joined, and a PublicId belongs to one
        // of them.
        //
        // The lookups include logically deleted identities so that a subject whose first request
        // suspended them is told AF-42c — the request they are repeating exists — rather than the
        // generic AF-42b. The endpoint admits such a caller because it is a data subject right; any
        // other deleted identity is refused with AF-42b once AF-42c has been ruled out.
        var person = await personReader.Query()
            .Include(x => x.ScopeOwnerships)
            .FirstOrDefaultAsync(x => x.PublicId == command.ActingPersonId);

        if (person is not null)
        {
            return person.ErasureRequestedAt is null && person.IsDeleted
                ? output.WithError(ErasureMessages.NotEligible)
                : await RequestForPersonAsync(output, command, person);
        }

        var googleUser = await googleUserReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ActingPersonId);

        return googleUser is null || (googleUser.ErasureRequestedAt is null && googleUser.IsDeleted)
            ? output.WithError(ErasureMessages.NotEligible)
            : await RequestForGoogleUserAsync(output, command, googleUser);
    }

    private async Task<DataOutput<RequestErasureCommandOutput?>> RequestForPersonAsync(
        DataOutput<RequestErasureCommandOutput?> output, RequestErasureCommand command, Person person)
    {
        // AF-42c. Checked before the credential so a repeat request does not cost an Argon2id
        // derivation, and so the answer does not depend on getting the password right a second time.
        if (person.ErasureRequestedAt is not null)
        {
            return output.WithError(ErasureMessages.ErasureAlreadyRequested);
        }

        // AF-42a. The same derivation UC-11 performs, through the same gate, so this endpoint cannot
        // be used to exhaust the process's memory any more than login can (Threat Model TH-03). It
        // is therefore governed by NFR-18 rather than NFR-05, like every password-verifying
        // endpoint.
        //
        // FR-AU-13: charged to the re-authentication budget first, and refused with the message
        // every re-authentication shares. A paused budget still has the password compared, and
        // refuses whatever the result.
        var admitted = await ReauthenticationBudget.TryChargeAsync(atomicWrites, person);

        if (string.IsNullOrWhiteSpace(command.Password) ||
            !await PasswordHashGate.Shared.TextMatchesAsync(command.Password, person.PasswordHash, person.Salt) ||
            !admitted)
        {
            return output.WithError(AuthMessages.PasswordOrCodeIncorrect);
        }

        await ReauthenticationBudget.ClearAsync(atomicWrites, person);

        var requestedAt = DateTime.UtcNow;
        var dueAt = requestedAt + retention.SubjectErasureDeadline;

        var restrictionLifted = person.ProcessingRestrictedAt is not null;

        // AF-42d (NFR-12): a scope must keep an owner. Suspending the last one would leave it
        // ownerless, so the suspension waits — but the request and its deadline do not.
        var blocked = await LastScopeOwnerGuard.WouldStripLastOwnerAsync(person, personReader);
        var blockedReason = blocked ? ErasureMessages.BlockedByLastScopeOwnership : null;

        // The request, the lifted restriction (see LiftRestriction), and the suspension or the
        // reason it waits, as one targeted write of those columns alone (#237) — made only while no
        // request is recorded and the person is live. A request recorded since the read refuses this
        // one as AF-42c, and a deletion as AF-42b, as either would have a moment earlier; the row is
        // read again, past the tracked copy, to tell which.
        if (!await atomicWrites.TryRecordErasureRequestAsync(person, requestedAt, dueAt, blockedReason))
        {
            var requestedSince = await personReader.Query()
                .Where(x => x.Id == person.Id)
                .Select(x => x.ErasureRequestedAt)
                .FirstOrDefaultAsync();

            return output.WithError(requestedSince is not null
                ? ErasureMessages.ErasureAlreadyRequested
                : ErasureMessages.NotEligible);
        }

        return Recorded(
            output, person.PublicId, requestedAt, dueAt, blocked, person.ErasureBlockedReason,
            restrictionLifted);
    }

    private async Task<DataOutput<RequestErasureCommandOutput?>> RequestForGoogleUserAsync(
        DataOutput<RequestErasureCommandOutput?> output, RequestErasureCommand command, GoogleUser googleUser)
    {
        // AF-42c.
        if (googleUser.ErasureRequestedAt is not null)
        {
            return output.WithError(ErasureMessages.ErasureAlreadyRequested);
        }

        // AF-42a. A Google User has no password, so the equivalent proof of presence is a token
        // Google minted for them just now. Verifying the signature is not enough on its own: the
        // subject claim has to name this very Google User, or any valid token for any Google account
        // would erase whichever account the bearer token happened to name.
        if (string.IsNullOrWhiteSpace(command.IdToken))
        {
            return output.WithError(ErasureMessages.CredentialNotAccepted);
        }

        var payload = await tokenVerifier.VerifyAsync(command.IdToken);

        if (payload is null || payload.Subject != googleUser.GoogleId)
        {
            return output.WithError(ErasureMessages.CredentialNotAccepted);
        }

        var requestedAt = DateTime.UtcNow;
        var dueAt = requestedAt + retention.SubjectErasureDeadline;

        googleUser.ErasureRequestedAt = requestedAt;
        googleUser.ErasureDueAt = dueAt;
        googleUser.UpdatedAt = requestedAt;

        var restrictionLifted = LiftRestriction(googleUser);

        // Nothing can block a Google User's erasure: FR-GO-04 makes them permanently User-equivalent,
        // so they own no scope and NFR-12 has nothing to protect.
        Suspend(googleUser, requestedAt);

        var update = await googleUserWriter.UpdateAsync(googleUser);

        return !update.Success
            ? output.WithErrors(update.Errors)
            : Recorded(
                output, googleUser.PublicId, requestedAt, dueAt, blocked: false, blockedReason: null,
                restrictionLifted);
    }

    /// <summary>
    ///     Lifts the subject's restriction on processing, if they have one, exactly as UC-45 does
    ///     when a subject lifts their own: the restriction and its ground are cleared, and no
    ///     notification is recorded because none is owed.
    /// </summary>
    /// <returns>Whether there was a restriction to lift.</returns>
    /// <remarks>
    ///     Done whether or not NFR-12 blocks the suspension (AF-42d). A blocked subject is then
    ///     neither restricted nor suspended, which is what lets them take part in the ownership
    ///     transfer (UC-21) that unblocks their own request.
    /// </remarks>
    private static bool LiftRestriction(GoogleUser googleUser)
    {
        if (googleUser.ProcessingRestrictedAt is null)
        {
            return false;
        }

        googleUser.ProcessingRestrictedAt = null;
        googleUser.RestrictionGround = null;
        googleUser.RestrictionLiftNotifiedAt = null;

        return true;
    }

    /// <summary>
    ///     Suspends the identity: the same logical deletion UC-09 and UC-28 perform, carrying the
    ///     kind that gives it the statutory deadline rather than the administrative window.
    /// </summary>
    /// <remarks>
    ///     Suspension is immediate and deliberate. A subject who has asked to be erased has
    ///     withdrawn the basis on which the account operates, so continuing to authenticate them
    ///     while the deadline runs would be processing they have objected to. `IsDeleted` already
    ///     stops every path: login refuses it, and `ActorLivenessFilter` refuses an unexpired token.
    /// </remarks>
    private static void Suspend(GoogleUser googleUser, DateTime at)
    {
        googleUser.IsDeleted = true;
        googleUser.DeletedAt = at;
        googleUser.DeletionKind = (int)DeletionKinds.SubjectRequested;
    }

    private static DataOutput<RequestErasureCommandOutput?> Recorded(
        DataOutput<RequestErasureCommandOutput?> output,
        Guid id,
        DateTime requestedAt,
        DateTime dueAt,
        bool blocked,
        string? blockedReason,
        bool restrictionLifted) =>
        output
            .WithData(new RequestErasureCommandOutput
            {
                Id = id,
                RequestedAt = requestedAt,
                DueAt = dueAt,
                Blocked = blocked,
                BlockedReason = blockedReason,
                RestrictionLifted = restrictionLifted
            })
            .WithMessage(blocked ? ErasureMessages.ErasureRequestedButBlocked : ErasureMessages.ErasureRequested);
}
