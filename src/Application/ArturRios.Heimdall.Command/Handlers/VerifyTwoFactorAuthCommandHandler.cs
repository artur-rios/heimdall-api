using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
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
/// </remarks>
public class VerifyTwoFactorAuthCommandHandler(
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncReadOnlyRepository<TwoFactorAuth> twoFactorReader,
    IAsyncRepository<TwoFactorEmailCode> emailCodeWriter,
    IAsyncRepository<TwoFactorRecoveryCode> recoveryCodeWriter,
    ITwoFactorFactorVerifier factorVerifier,
    ITwoFactorChallengeTokenValidator challengeTokenValidator,
    PersonAuthTokenService personAuthTokenService)
    : ICommandHandlerAsync<VerifyTwoFactorAuthCommand, VerifyTwoFactorAuthCommandOutput>
{
    public async Task<DataOutput<VerifyTwoFactorAuthCommandOutput?>> HandleAsync(
        VerifyTwoFactorAuthCommand command)
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

        if (person is null || twoFactorAuth is null)
        {
            return output.WithError(TwoFactorMessages.ChallengeTokenInvalid);
        }

        // AF-38b/AF-38c: an app code, a live email code, or an unused recovery code — or the same
        // rejection either way.
        var verification = await factorVerifier.VerifyAsync(twoFactorAuth, command.Code, command.RecoveryCode);

        if (!verification.Matched)
        {
            return output.WithError(TwoFactorMessages.FactorInvalid);
        }

        var consumedEmailCode = verification.ConsumedEmailCode;
        var consumedRecoveryCode = verification.ConsumedRecoveryCode;

        // UC-11 step 6 / UC-38 step 5 (FR-2F-09): the same scope-eligibility rules a direct login
        // enforces still apply to a 2FA-gated one. The factor already checked out, so this is a
        // distinct rejection from AF-38a rather than a reuse of ChallengeTokenInvalid.
        if (!personAuthTokenService.TryBuildSubject(person, out var subject))
        {
            return output.WithError(TwoFactorMessages.ScopeNoLongerEligible);
        }

        // UC-38 step 4: a recovery code can never be replayed.
        if (consumedRecoveryCode is not null)
        {
            consumedRecoveryCode.Used = true;
            consumedRecoveryCode.UsedAt = DateTime.UtcNow;

            var recoveryUpdate = await recoveryCodeWriter.UpdateAsync(consumedRecoveryCode);

            if (!recoveryUpdate.Success)
            {
                return output.WithErrors(recoveryUpdate.Errors);
            }
        }

        // The email code that completed this login can never be replayed either, the same way
        // ConfirmTwoFactorAuthCommandHandler retires the one that confirmed setup.
        if (consumedEmailCode is not null)
        {
            consumedEmailCode.Used = true;

            var emailUpdate = await emailCodeWriter.UpdateAsync(consumedEmailCode);

            if (!emailUpdate.Success)
            {
                return output.WithErrors(emailUpdate.Errors);
            }
        }

        var token = await personAuthTokenService.IssueAsync(subject!);

        return output
            .WithData(new VerifyTwoFactorAuthCommandOutput { Token = token.Token, ExpiresAt = token.ExpiresAt })
            .WithMessage(TwoFactorMessages.VerificationSuccessful);
    }
}
