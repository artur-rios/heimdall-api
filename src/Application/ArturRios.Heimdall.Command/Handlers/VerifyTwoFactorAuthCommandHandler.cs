using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="VerifyTwoFactorAuthCommand" /> (UC-38, FR-2F-09): validates the challenge
///     token AF-11g issued at login (AF-38a), matches the submitted app code, email code, or recovery
///     code against the caller's active <see cref="TwoFactorAuth" /> configuration via
///     <see cref="ITwoFactorFactorVerifier" /> (AF-38b/AF-38c), and — only once a factor checks out —
///     issues the full authentication token through <see cref="PersonAuthTokenService" />, the same
///     service <c>LoginCommandHandler</c> uses, so a 2FA-gated login ends exactly like a direct one.
/// </summary>
/// <remarks>
///     AF-38b (wrong or missing code) and AF-38c (an already-used recovery code) answer identically —
///     <see cref="TwoFactorMessages.FactorInvalid" />, 401 — so a caller cannot distinguish a wrong
///     code from a reused recovery code, exactly the reasoning UC-11's AF-11a…AF-11e collapse into
///     one message for. A person or 2FA configuration the challenge token names but that no longer
///     resolves is treated the same as an invalid challenge (AF-38a). A person and configuration that
///     do resolve, and a factor that genuinely checks out, but whose scope eligibility (UC-11's own
///     AF-11d/AF-11e) no longer holds, gets its own <see cref="TwoFactorMessages.ScopeNoLongerEligible" />
///     instead — the token and the factor were both valid, so collapsing that case into "challenge
///     invalid" would misdescribe what actually happened.
///     <para>
///         <b>Once, under concurrency too.</b> Redeeming the challenge and spending the recovery or
///         email code are conditional writes (<see cref="IAtomicWrites" />): of any number of
///         simultaneous requests presenting the same challenge or the same code, exactly one gets a
///         token. Checking in memory and then saving let several through, since each had read the row
///         before any of them wrote it.
///     </para>
///     <para>
///         <b>Five guesses per challenge</b> (FR-2F-17) with an app code or a recovery code
///         (<see cref="MaxGuessesPerChallenge" />), the cap FR-2F-13 already puts on an email code.
///         Each such guess is reserved on the challenge before it is compared, so parallel guesses
///         cannot outrun the cap; at the cap the challenge is cleared and its token refused as AF-38a,
///         and further guessing costs a fresh password check. An email-only configuration's guesses
///         are not charged here: the email code's own cap already bounds them.
///     </para>
/// </remarks>
public class VerifyTwoFactorAuthCommandHandler(
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<TwoFactorAuth, long> twoFactorReader,
    IAtomicWrites atomicWrites,
    ITwoFactorFactorVerifier factorVerifier,
    ITwoFactorChallengeTokenValidator challengeTokenValidator,
    PersonAuthTokenService personAuthTokenService)
    : ICommandHandlerAsync<VerifyTwoFactorAuthCommand, VerifyTwoFactorAuthCommandOutput>
{
    /// <summary>
    ///     Guesses one challenge allows with an app code or a recovery code — the same five FR-2F-13
    ///     allows an email code (<see cref="TwoFactorEmailCodeVerification.MaxFailedAttempts" />).
    /// </summary>
    public const int MaxGuessesPerChallenge = TwoFactorEmailCodeVerification.MaxFailedAttempts;

    public async Task<DataOutput<VerifyTwoFactorAuthCommandOutput?>> HandleAsync(
        VerifyTwoFactorAuthCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<VerifyTwoFactorAuthCommandOutput?>.New;

        // AF-38a: signature, expiry, and the MFA-pending claim.
        var principal = await challengeTokenValidator.ValidateAsync(command.ChallengeToken);

        if (principal is null)
        {
            return output.WithError(TwoFactorMessages.ChallengeTokenInvalid);
        }

        var person = await personReader.Query()
            .Include(person => person.ScopeMembership)
            .ThenInclude(membership => membership!.Scope)
            .Include(person => person.ScopeOwnerships)
            .ThenInclude(ownership => ownership.Scope)
            .FirstOrDefaultAsync(person => person.PublicId == principal.PersonId && !person.IsDeleted);

        var twoFactorAuth = person is null
            ? null
            : await twoFactorReader.Query()
                .FirstOrDefaultAsync(x => x.PersonId == person.Id && x.IsActive);

        // AF-38a again, for what a signature cannot say. A person under a restriction of processing
        // may not authenticate (NFR-24, UC-44 step 4) — UC-11 refuses them, and one restricted after
        // their challenge was issued is refused here the same way rather than handed a full token.
        // And a challenge is redeemable once (FR-2F-10): the token must still name the
        // configuration's outstanding challenge, which redemption clears and a newer login replaces.
        if (person is null || twoFactorAuth is null ||
            person.ProcessingRestrictedAt is not null ||
            twoFactorAuth.ChallengeId != principal.ChallengeId)
        {
            return output.WithError(TwoFactorMessages.ChallengeTokenInvalid);
        }

        var challengeId = principal.ChallengeId;

        // FR-2F-17: a guess with an app code or a recovery code is charged to the challenge before it is
        // compared. A challenge that has had its five — or that a parallel request has just redeemed
        // or retired — is refused as AF-38a, without comparing anything.
        var chargedToChallenge = !string.IsNullOrWhiteSpace(command.RecoveryCode) || twoFactorAuth.AppEnabled;

        if (chargedToChallenge &&
            !await atomicWrites.TryChargeChallengeGuessAsync(twoFactorAuth, challengeId, MaxGuessesPerChallenge))
        {
            return output.WithError(TwoFactorMessages.ChallengeTokenInvalid);
        }

        // AF-38b/AF-38c: an app code, a live email code, or an unused recovery code — or the same
        // rejection either way.
        var verification = await factorVerifier.VerifyAsync(twoFactorAuth, command.Code, command.RecoveryCode);

        if (!verification.Matched)
        {
            if (chargedToChallenge)
            {
                await atomicWrites.ExpireChallengeIfExhaustedAsync(twoFactorAuth, challengeId, MaxGuessesPerChallenge);
            }

            return output.WithError(TwoFactorMessages.FactorInvalid);
        }

        // UC-11 step 6 / UC-38 step 5 (FR-2F-09): the same scope-eligibility rules a direct login
        // enforces still apply to a 2FA-gated one. The factor already checked out, so this is a
        // distinct rejection from AF-38a rather than a reuse of ChallengeTokenInvalid.
        if (!personAuthTokenService.TryBuildSubject(person, out var subject))
        {
            return output.WithError(TwoFactorMessages.ScopeNoLongerEligible);
        }

        // FR-2F-10: the challenge is spent — by exactly one request, however many arrive together.
        // Before the factor is spent, so a request that loses here has burned nothing of the
        // caller's; and before anything else is written, so a failure further down leaves the caller
        // to log in again rather than holding a challenge still redeemable.
        if (!await atomicWrites.TryRedeemChallengeAsync(twoFactorAuth, challengeId))
        {
            return output.WithError(TwoFactorMessages.ChallengeTokenInvalid);
        }

        // UC-38 step 4: a recovery code can never be replayed — nor spent twice at once, here and in
        // UC-40 or another challenge's verification.
        if (verification.ConsumedRecoveryCode is { } consumedRecoveryCode &&
            !await atomicWrites.TryConsumeRecoveryCodeAsync(consumedRecoveryCode, DateTime.UtcNow))
        {
            return output.WithError(TwoFactorMessages.FactorInvalid);
        }

        // The email code that completed this login can never be replayed either, the same way
        // ConfirmTwoFactorAuthCommandHandler retires the one that confirmed setup.
        if (verification.ConsumedEmailCode is { } consumedEmailCode &&
            !await atomicWrites.TryConsumeEmailCodeAsync(consumedEmailCode))
        {
            return output.WithError(TwoFactorMessages.FactorInvalid);
        }

        var token = await personAuthTokenService.IssueAsync(subject!);

        return output
            .WithData(new VerifyTwoFactorAuthCommandOutput
            {
                Token = token.Token, ExpiresAt = token.ExpiresAt, EmailVerified = person.EmailVerified
            })
            .WithMessage(TwoFactorMessages.VerificationSuccessful);
    }
}
