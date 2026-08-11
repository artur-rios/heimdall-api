using System.Security.Cryptography;
using System.Text;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using Microsoft.EntityFrameworkCore;
using OtpNet;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="VerifyTwoFactorAuthCommand" /> (UC-38, FR-2F-09): validates the challenge
///     token AF-11g issued at login (AF-38a), matches the submitted app code, email code, or recovery
///     code against the caller's active <see cref="TwoFactorAuth" /> configuration (AF-38b/AF-38c),
///     and — only once a factor checks out — issues the full authentication token through
///     <see cref="PersonAuthTokenService" />, the same service <c>LoginCommandHandler</c> uses, so a
///     2FA-gated login ends exactly like a direct one.
/// </summary>
/// <remarks>
///     AF-38b (wrong or missing code) and AF-38c (an already-used recovery code) answer identically —
///     <see cref="TwoFactorMessages.FactorInvalid" />, 401 — so a caller cannot distinguish a wrong
///     code from a reused recovery code, exactly the reasoning UC-11's AF-11a…AF-11e collapse into
///     one message for. A challenge token whose person (or 2FA configuration) no longer qualifies by
///     the time this runs is treated the same as an invalid challenge (AF-38a) rather than inventing
///     a fourth rejection UC-38 does not define.
/// </remarks>
public class VerifyTwoFactorAuthCommandHandler(
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncReadOnlyRepository<TwoFactorAuth> twoFactorReader,
    IAsyncReadOnlyRepository<TwoFactorEmailCode> emailCodeReader,
    IAsyncRepository<TwoFactorEmailCode> emailCodeWriter,
    IAsyncReadOnlyRepository<TwoFactorRecoveryCode> recoveryCodeReader,
    IAsyncRepository<TwoFactorRecoveryCode> recoveryCodeWriter,
    ITotpSecretProtector totpSecretProtector,
    ITwoFactorChallengeTokenValidator challengeTokenValidator,
    PersonAuthTokenService personAuthTokenService)
    : ICommandHandlerAsync<VerifyTwoFactorAuthCommand, VerifyTwoFactorAuthCommandOutput>
{
    // Same one time-step (30s) tolerance ConfirmTwoFactorAuthCommandHandler allows for clock drift.
    private static readonly VerificationWindow TotpVerificationWindow = new(previous: 1, future: 1);

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

        TwoFactorEmailCode? consumedEmailCode = null;
        TwoFactorRecoveryCode? consumedRecoveryCode = null;

        if (!string.IsNullOrWhiteSpace(command.RecoveryCode))
        {
            // AF-38b/AF-38c: an unused, matching recovery code — or the same rejection either way.
            consumedRecoveryCode = await FindMatchingRecoveryCodeAsync(twoFactorAuth.Id, command.RecoveryCode);

            if (consumedRecoveryCode is null)
            {
                return output.WithError(TwoFactorMessages.FactorInvalid);
            }
        }
        else
        {
            var appMatches = twoFactorAuth.AppEnabled && VerifyAppCode(twoFactorAuth, command.Code);

            if (!appMatches && twoFactorAuth.EmailEnabled)
            {
                consumedEmailCode = await FindMatchingEmailCodeAsync(twoFactorAuth.Id, command.Code);
            }

            // AF-38b: neither the App nor the Email method matched.
            if (!appMatches && consumedEmailCode is null)
            {
                return output.WithError(TwoFactorMessages.FactorInvalid);
            }
        }

        // UC-11 step 6 / UC-38 step 5 (FR-2F-09): the same scope-eligibility rules a direct login
        // enforces still apply to a 2FA-gated one.
        if (!personAuthTokenService.TryBuildSubject(person, out var subject))
        {
            return output.WithError(TwoFactorMessages.ChallengeTokenInvalid);
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

    /// <summary>Decrypts the stored TOTP secret and checks <paramref name="code" /> against it (FR-2F-09).</summary>
    private bool VerifyAppCode(TwoFactorAuth twoFactorAuth, string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || twoFactorAuth.TotpSecretEncrypted is null)
        {
            return false;
        }

        string base32Secret;

        try
        {
            base32Secret = totpSecretProtector.Unprotect(twoFactorAuth.TotpSecretEncrypted);
        }
        catch (CryptographicException)
        {
            // A corrupted or otherwise unreadable TotpSecretEncrypted value (e.g. protected under a
            // Data Protection key that is no longer available) can never be decrypted back into a
            // valid app code, so it fails the same way a wrong code does (AF-38b) instead of
            // surfacing as a 500.
            return false;
        }

        var totp = new Totp(Base32Encoding.ToBytes(base32Secret));

        return totp.VerifyTotp(code, out _, TotpVerificationWindow);
    }

    /// <summary>
    ///     Finds a not-yet-used, not-yet-expired email code for this configuration that
    ///     <paramref name="code" /> hashes to — the same comparison
    ///     <see cref="ConfirmTwoFactorAuthCommandHandler" /> uses. Returns <c>null</c> for a missing,
    ///     incorrect, expired, or already-used code, all alike (AF-38b).
    /// </summary>
    private async Task<TwoFactorEmailCode?> FindMatchingEmailCodeAsync(long twoFactorAuthId, string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var now = DateTime.UtcNow;

        var live = await emailCodeReader.Query()
            .Where(x => x.TwoFactorAuthId == twoFactorAuthId && !x.Used && x.ExpiresAt > now)
            .ToListAsync();

        return live.FirstOrDefault(x => Hash.TextMatches(code, x.CodeHash, x.Salt));
    }

    /// <summary>
    ///     Finds an unused recovery code for this configuration whose hash matches
    ///     <paramref name="recoveryCode" /> — the same SHA-256 digest
    ///     <see cref="ConfirmTwoFactorAuthCommandHandler" /> stores recovery codes with. An unknown
    ///     code and an already-used one both return <c>null</c> here — the query itself excludes used
    ///     rows, so the two cases are indistinguishable by construction (AF-38c).
    /// </summary>
    private async Task<TwoFactorRecoveryCode?> FindMatchingRecoveryCodeAsync(
        long twoFactorAuthId, string recoveryCode)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(recoveryCode));

        var unused = await recoveryCodeReader.Query()
            .Where(x => x.TwoFactorAuthId == twoFactorAuthId && !x.Used)
            .ToListAsync();

        return unused.FirstOrDefault(x => x.CodeHash.SequenceEqual(hash));
    }
}
