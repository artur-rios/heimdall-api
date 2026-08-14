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
///     The endpoint is anonymous, so it is careful about what a rejection reveals: AF-25a and AF-25d
///     share <see cref="AuthMessages.GoogleAuthenticationFailed" /> and AF-25b answers alike for a
///     missing, deleted, and disabled scope. AF-25c is named separately — the caller has proved the
///     address is theirs, so being told it is taken tells them only about themselves.
/// </remarks>
public class GoogleSignInCommandHandler(
    IGoogleIdTokenVerifier tokenVerifier,
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncReadOnlyRepository<GoogleUser> googleUserReader,
    IAsyncRepository<GoogleUser> googleUserWriter,
    IAuthTokenIssuer tokenIssuer)
    : ICommandHandlerAsync<GoogleSignInCommand, GoogleSignInCommandOutput>
{
    public async Task<DataOutput<GoogleSignInCommandOutput?>> HandleAsync(GoogleSignInCommand command)
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
        // ownership is a ScopeAdmin concept.
        var token = await tokenIssuer.IssueAsync(new AuthTokenSubject(
            googleUser.PublicId,
            (int)Roles.User,
            scope.PublicId,
            []));

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
    ///     Google User is created from the verified claims (FR-GO-05) after the address is confirmed
    ///     free (AF-25c).
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
        var email = payload.Email.ToLower();

        // AF-25c (FR-GO-07): the address must be free within the scope, considered jointly with the
        // scope's User persons. Two reads rather than one because the rule spans two tables — the
        // unique index on (scope_id, email) only covers the Google User half. Compared
        // case-insensitively (LOWER() in SQL), matching CreateUserCommandHandler.
        var takenByGoogleUser = await googleUserReader.Query()
            .AnyAsync(x => x.ScopeId == scope.Id && !x.IsDeleted && x.Email.ToLower() == email);

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
        // who granted only the email scope still gets an account, with the fields left empty. An
        // absent email_verified claim becomes false: the column is not nullable, and there is
        // nothing stored yet to preserve, so a brand-new row starts unverified — the value it would
        // have held before FR-GO-19 existed.
        var newGoogleUser = new GoogleUser
        {
            GoogleId = payload.Subject,
            Name = payload.Name ?? string.Empty,
            Email = payload.Email,
            EmailVerified = payload.EmailVerified ?? false,
            ProfilePictureUrl = payload.PictureUrl,
            ScopeId = scope.Id
        };

        var creation = await googleUserWriter.CreateAsync(newGoogleUser);

        return creation.Success
            ? (newGoogleUser, null)
            : (null, output.WithErrors(creation.Errors));
    }
}
