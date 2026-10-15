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
///     Handles <see cref="GoogleSignInCommand" /> (UC-25, FR-GO-03…FR-GO-13): verifies the Google ID
///     token, confirms the target scope allows Google sign-in, then either creates a Google User from
///     the token's claims (sign-up, FR-GO-09) or authenticates the one already registered for that
///     Google account in that scope (sign-in, FR-GO-10), and issues an authentication token claiming
///     the <c>User</c> role.
/// </summary>
/// <remarks>
///     The endpoint is anonymous, so it is careful about what a rejection reveals: AF-25a, AF-25d
///     and AF-25g share <see cref="AuthMessages.GoogleAuthenticationFailed" /> and AF-25b answers
///     alike for a missing, deleted, and disabled scope. AF-25c is named separately — the caller has
///     proved the address is theirs, so being told it is taken tells them only about themselves —
///     and that proof is why AF-25f comes first: a token whose address Google does not vouch for is
///     refused before the address is compared with anything.
/// </remarks>
public class GoogleSignInCommandHandler(
    IGoogleIdTokenVerifier tokenVerifier,
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<GoogleUser, long> googleUserReader,
    IAsyncRepository<GoogleUser, long> googleUserWriter,
    IAuthTokenIssuer tokenIssuer)
    : ICommandHandlerAsync<GoogleSignInCommand, GoogleSignInCommandOutput>
{
    public async Task<DataOutput<GoogleSignInCommandOutput?>> HandleAsync(GoogleSignInCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<GoogleSignInCommandOutput?>.New;

        // UC-25 step 3 (AF-25a, FR-GO-11, NFR-13): verify signature, issuer, audience, and expiry
        // before any claim is trusted. Runs before the scope is read, as the specification's sequence
        // diagram does — an unverified caller learns nothing about which scopes exist. This is also
        // what answers a request that omitted the token entirely, so UC-25 needs no 400 flow.
        var payload = await tokenVerifier.VerifyAsync(command.IdToken);

        if (payload is null)
        {
            return output.WithError(AuthMessages.GoogleAuthenticationFailed);
        }

        // AF-25g (FR-GO-20): a caller that names a nonce is owed a token issued for it, so one
        // obtained for any other sign-in — with another nonce, or none — is refused as an
        // unverifiable token is. Ordinal: the nonce is an opaque value the client chose. A request
        // without one compares nothing.
        if (!string.IsNullOrEmpty(command.Nonce) &&
            !string.Equals(payload.Nonce, command.Nonce, StringComparison.Ordinal))
        {
            return output.WithError(AuthMessages.GoogleAuthenticationFailed);
        }

        // UC-25 step 4 (AF-25b, FR-GO-03/FR-GO-13): the scope must exist, be active, and have the
        // setting UC-24 writes switched on. The alternative flow names all three conditions as one
        // outcome, so the filter answers for all three.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x =>
                x.PublicId == command.ScopeId && !x.IsDeleted && x.GoogleSignInEnabled);

        if (scope is null)
        {
            return output.WithError(AuthMessages.GoogleSignInUnavailable);
        }

        // UC-25 step 5: look the account up by Google's 'sub' within the scope (FR-GO-08). The lookup
        // deliberately omits an !IsDeleted filter: AF-25d exists to reject a logically deleted Google
        // User, so it must be found first — the same reason UC-11's person lookup omits one.
        var googleUser = await googleUserReader.Query()
            .FirstOrDefaultAsync(x => x.ScopeId == scope.Id && x.GoogleId == payload.Subject);

        if (googleUser is null)
        {
            // UC-25 step 6 (sign-up).
            var signUp = await SignUpAsync(payload, scope, output);

            if (signUp.googleUser is null)
            {
                return signUp.failure!;
            }

            googleUser = signUp.googleUser;
        }
        // NFR-20: an anonymised account is nobody's any more, and its Google subject is no longer
        // stored, so the lookup should never find one; this guards the day that assumption breaks.
        else if (googleUser.AnonymisedAt is not null)
        {
            return output.WithError(AuthMessages.GoogleAuthenticationFailed);
        }
        // NFR-24 (UC-44 step 4): processing is restricted, so the account may not authenticate for
        // anything but its subject's own rights over the record — the same subject-rights-only
        // token UC-11 issues a restricted person, for the same reason: without it nobody could lift
        // their own restriction (UC-45) once their earlier tokens had expired. Checked before
        // deletion, because a suspended subject keeps those rights until the record is anonymised.
        // Nothing is refreshed from the ID token first: that would be processing the record.
        else if (googleUser.ProcessingRestrictedAt is not null)
        {
            var rightsOnly = await tokenIssuer.IssueSubjectRightsOnlyAsync(
                googleUser.PublicId, (int)Roles.User, securityStamp: null);

            return output
                .WithData(new GoogleSignInCommandOutput
                {
                    Token = rightsOnly.Token,
                    ExpiresAt = rightsOnly.ExpiresAt,
                    EmailVerified = googleUser.EmailVerified,
                    ProcessingRestricted = true
                })
                .WithMessage(AuthMessages.SubjectRightsOnlySignIn);
        }
        // UC-25 step 7 (AF-25d, FR-GO-12): the account exists but has been logically deleted.
        else if (googleUser.IsDeleted)
        {
            return output.WithError(AuthMessages.GoogleAuthenticationFailed);
        }

        // FR-GO-19: the row was populated from the token at sign-up (FR-GO-09) and never touched
        // again, so a Google account verified after its first sign-in here kept a stale flag. The
        // token just verified is the fresher truth, so the column is brought back into line — a
        // no-op on a sign-up, where the two already agree by construction, and a no-op when the
        // token carries no email_verified claim at all.
        await RefreshEmailVerifiedAsync(googleUser, payload);

        // UC-25 step 8 (FR-GO-04): the token claims the Google User's PublicId, the User role — a
        // Google account is never a ScopeAdmin or SystemAdmin — and the one scope it belongs to.
        // Internal bigint Ids never reach a token (NFR-15), and OwnedScopeIds is empty because
        // ownership is a ScopeAdmin concept. No security stamp (FR-AU-11): a Google User has no
        // credential here that could change, so there is nothing for one to revoke on.
        var token = await tokenIssuer.IssueAsync(new AuthTokenSubject(
            googleUser.PublicId,
            string.IsNullOrWhiteSpace(googleUser.Name) ? googleUser.Email : googleUser.Name,
            (int)Roles.User,
            scope.PublicId,
            [],
            SecurityStamp: null));

        return output
            .WithData(new GoogleSignInCommandOutput
            {
                // The stored flag, which the refresh above has already brought into line with the
                // token whenever the token had something to say. Reporting the row rather than the
                // claim keeps the response answerable when the claim is absent, without the
                // published field having to become nullable.
                Token = token.Token, ExpiresAt = token.ExpiresAt, EmailVerified = googleUser.EmailVerified
            })
            .WithMessage(AuthMessages.GoogleSignInSuccessful);
    }

    /// <summary>
    ///     FR-GO-19: writes the verified token's <c>email_verified</c> back to the stored row when
    ///     the two disagree, so a returning Google User's flag does not stay frozen at whatever it
    ///     was on their first sign-in. Nothing is written when they already agree, keeping the
    ///     ordinary sign-in path read-only, and nothing is written when the token carries no such
    ///     claim — silence is not an assertion that the address is unverified, so a caller whose
    ///     client asked for a token without the <c>email</c> scope keeps whatever is stored.
    /// </summary>
    /// <remarks>
    ///     A failed write does not fail the sign-in. The caller has proved the account is theirs and
    ///     the token is theirs to receive; a flag that could not be refreshed is a data-freshness
    ///     problem, not an authentication one — the same judgement <c>EmailVerificationService</c>
    ///     and <c>PasswordResetService</c> make about the writes they discard the result of. The
    ///     response reports the in-memory row either way, so the caller is told the value this
    ///     sign-in settled on regardless of whether the write landed.
    /// </remarks>
    private async Task RefreshEmailVerifiedAsync(GoogleUser googleUser, GoogleIdTokenPayload payload)
    {
        if (payload.EmailVerified is not { } claimed || googleUser.EmailVerified == claimed)
        {
            return;
        }

        googleUser.EmailVerified = claimed;

        await googleUserWriter.UpdateAsync(googleUser);
    }

    /// <summary>
    ///     UC-25 step 6 (FR-GO-09): first sign-in with this Google account in this scope, so the
    ///     Google User is created from the verified claims (FR-GO-05) once Google vouches for the
    ///     address (AF-25f) and it is confirmed free (AF-25c).
    /// </summary>
    /// <returns>
    ///     The created Google User, or the failure output to return instead. A tuple rather than an
    ///     exception because handlers in this project report failures on the
    ///     <see cref="DataOutput{T}" />.
    /// </returns>
    private async Task<(GoogleUser? googleUser, DataOutput<GoogleSignInCommandOutput?>? failure)>
        SignUpAsync(
            GoogleIdTokenPayload payload, Scope scope, DataOutput<GoogleSignInCommandOutput?> output)
    {
        // AF-25f (FR-GO-07): only an address Google vouches for may take a place in the scope. An
        // absent claim proves nothing, so it is refused with a false one. First, so that a token for
        // an unproven address cannot ask whether the address is taken either.
        if (payload.EmailVerified is not true)
        {
            return (null, output.WithError(AuthMessages.GoogleEmailNotVerified));
        }

        var email = payload.Email.ToLower();

        // AF-25c (FR-GO-07): the address must be free within the scope, considered jointly with the
        // scope's User persons. Two reads rather than one because the rule spans two tables — the
        // unique index on (scope_id, lower(email)) only covers the Google User half. Compared
        // case-insensitively (LOWER() in SQL), matching CreateUserCommandHandler.
        //
        // Logically deleted Google Users count as taking the address, deliberately and unlike the
        // person half below. That index does not exclude them either, so a check that did would
        // answer "free" and then fail on the insert — a persistence error in place of AF-25c's 409,
        // for a caller who could never get past it. FR-GO-12 keeps a deleted row addressable rather
        // than erasing it, so the address it holds stays taken until UC-29 removes the row for good.
        var takenByGoogleUser = await googleUserReader.Query()
            .AnyAsync(x => x.ScopeId == scope.Id && x.Email.ToLower() == email);

        if (takenByGoogleUser)
        {
            return (null, output.WithError(AuthMessages.EmailAlreadyExists));
        }

        var takenByPerson = await personReader.Query().AnyAsync(person =>
            !person.IsDeleted && person.Email.ToLower() == email &&
            person.ScopeMembership != null && person.ScopeMembership.ScopeId == scope.Id);

        if (takenByPerson)
        {
            return (null, output.WithError(AuthMessages.EmailAlreadyExists));
        }

        // FR-GO-05/06: every field comes from the verified token, and the row is bound to the one
        // scope the sign-in was initiated in. Name and picture are optional on the token — a caller
        // who granted only the email scope still gets an account, with the fields left empty. The
        // address is verified: AF-25f admitted nothing else.
        var newGoogleUser = new GoogleUser
        {
            GoogleId = payload.Subject,
            Name = payload.Name ?? string.Empty,
            Email = payload.Email,
            EmailVerified = true,
            ProfilePictureUrl = payload.PictureUrl,
            ScopeId = scope.Id
        };

        // NFR-23: recorded here as everywhere else. UC-25 creates an identity without any
        // administrator involved, so it is the path where an unrecorded basis would be easiest to
        // end up with and hardest to notice.
        LegalBasisRecorder.Record(newGoogleUser, scope, DateTime.UtcNow);

        var creation = await googleUserWriter.CreateAsync(newGoogleUser);

        return creation.Success
            ? (newGoogleUser, null)
            : (null, output.WithErrors(creation.Errors));
    }
}
