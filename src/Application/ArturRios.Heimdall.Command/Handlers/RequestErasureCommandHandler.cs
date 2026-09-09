using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="RequestErasureCommand" /> (UC-42): re-authenticates the caller, records
///     their erasure request against their own record, and suspends the identity so the request
///     takes effect immediately. The anonymisation pass (NFR-20) completes it on the deadline. All
///     failures are returned as errors on the <see cref="DataOutput{T}" /> rather than thrown.
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
///         Both failures answer alike.
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
///         <b>Why the deadline is stored rather than derived.</b> The obligation attaches when the
///         request is made. Recomputing it from the current configuration on every read would let a
///         later change move an obligation that had already attached.
///     </para>
/// </remarks>
public class RequestErasureCommandHandler(
    IValidator<RequestErasureCommand> validator,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Person> personWriter,
    IAsyncReadOnlyRepository<GoogleUser> googleUserReader,
    IAsyncRepository<GoogleUser> googleUserWriter,
    IGoogleIdTokenVerifier tokenVerifier,
    DataRetentionOptions retention)
    : ICommandHandlerAsync<RequestErasureCommand, RequestErasureCommandOutput>
{
    public async Task<DataOutput<RequestErasureCommandOutput?>> HandleAsync(RequestErasureCommand command)
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
        var person = await personReader.Query()
            .Include(x => x.ScopeOwnerships)
            .FirstOrDefaultAsync(x => x.PublicId == command.ActingPersonId && !x.IsDeleted);

        if (person is not null)
        {
            return await RequestForPersonAsync(output, command, person);
        }

        var googleUser = await googleUserReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ActingPersonId && !x.IsDeleted);

        return googleUser is not null
            ? await RequestForGoogleUserAsync(output, command, googleUser)
            : output.WithError(ErasureMessages.NotEligible);
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
        if (string.IsNullOrWhiteSpace(command.Password) ||
            !await PasswordHashGate.Shared.TextMatchesAsync(command.Password, person.PasswordHash, person.Salt))
        {
            return output.WithError(ErasureMessages.CredentialNotAccepted);
        }

        var requestedAt = DateTime.UtcNow;
        var dueAt = requestedAt + retention.SubjectErasureDeadline;

        person.ErasureRequestedAt = requestedAt;
        person.ErasureDueAt = dueAt;
        person.UpdatedAt = requestedAt;

        // AF-42d (NFR-12): a scope must keep an owner. Suspending the last one would leave it
        // ownerless, so the suspension waits — but the request and its deadline do not.
        var blocked = await LastScopeOwnerGuard.WouldStripLastOwnerAsync(person, personReader);

        if (blocked)
        {
            person.ErasureBlockedReason = ErasureMessages.BlockedByLastScopeOwnership;
        }
        else
        {
            Suspend(person, requestedAt);
        }

        var update = await personWriter.UpdateAsync(person);

        return !update.Success
            ? output.WithErrors(update.Errors)
            : Recorded(output, person.PublicId, requestedAt, dueAt, blocked, person.ErasureBlockedReason);
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

        // Nothing can block a Google User's erasure: FR-GO-04 makes them permanently User-equivalent,
        // so they own no scope and NFR-12 has nothing to protect.
        Suspend(googleUser, requestedAt);

        var update = await googleUserWriter.UpdateAsync(googleUser);

        return !update.Success
            ? output.WithErrors(update.Errors)
            : Recorded(output, googleUser.PublicId, requestedAt, dueAt, blocked: false, blockedReason: null);
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
    private static void Suspend(Person person, DateTime at)
    {
        person.IsDeleted = true;
        person.DeletedAt = at;
        person.DeletionKind = (int)DeletionKinds.SubjectRequested;
    }

    /// <inheritdoc cref="Suspend(Person, DateTime)" />
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
        string? blockedReason) =>
        output
            .WithData(new RequestErasureCommandOutput
            {
                Id = id,
                RequestedAt = requestedAt,
                DueAt = dueAt,
                Blocked = blocked,
                BlockedReason = blockedReason
            })
            .WithMessage(blocked ? ErasureMessages.ErasureRequestedButBlocked : ErasureMessages.ErasureRequested);
}
