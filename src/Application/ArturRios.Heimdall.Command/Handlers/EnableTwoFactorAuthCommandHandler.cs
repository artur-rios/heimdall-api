using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Random;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using OtpNet;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="EnableTwoFactorAuthCommand" /> (UC-36, FR-2F-01…FR-2F-03): validates input
///     (AF-36c), confirms the caller is a live person eligible to opt in (AF-36b), refuses a caller
///     whose configuration is already active (AF-36a), then creates or overwrites (AF-36d) a pending
///     <see cref="TwoFactorAuth" /> row for the method(s) selected — generating and encrypting a TOTP
///     secret for the App method, and issuing a fresh 6-digit email code for the Email method.
/// </summary>
/// <remarks>
///     <para>
///         A Google User is never resolvable here: <see cref="GoogleUser" /> and <see cref="Person" />
///         are separate tables with separate <c>PublicId</c> spaces, and a Google-issued token's
///         subject names a <see cref="GoogleUser" />, not a <see cref="Person" /> (UC-25 step 8). The
///         person lookup below therefore already excludes every Google User — there is no live Google
///         session that could reach this handler with a person-shaped actor to begin with — and the
///         same lookup covers a bearer token naming a person who no longer exists, exactly as
///         <see cref="ResendVerificationEmailCommandHandler" />'s does. Both answer AF-36b's 403 alike,
///         so the endpoint never distinguishes "you're a Google User" from "you don't exist" from a
///         caller who could exploit the difference.
///     </para>
///     <para>
///         Nothing is activated here — <see cref="TwoFactorAuth.IsActive" /> stays <c>false</c> until
///         UC-37 confirms every selected method's code. AF-36d re-initiation is handled by finding the
///         caller's not-yet-active row (if any) and updating it in place rather than inserting a
///         second one, since §4.9 allows at most one row per person.
///     </para>
/// </remarks>
public class EnableTwoFactorAuthCommandHandler(
    IValidator<EnableTwoFactorAuthCommand> validator,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncReadOnlyRepository<TwoFactorAuth> twoFactorReader,
    IAsyncRepository<TwoFactorAuth> twoFactorWriter,
    IAsyncReadOnlyRepository<TwoFactorEmailCode> emailCodeReader,
    IAsyncRepository<TwoFactorEmailCode> emailCodeWriter,
    ITotpSecretProtector totpSecretProtector,
    ITwoFactorEmailSender emailSender)
    : ICommandHandlerAsync<EnableTwoFactorAuthCommand, EnableTwoFactorAuthCommandOutput>
{
    private const string Issuer = "Heimdall";
    private const int TotpSecretLengthInBytes = 20; // 160 bits, the RFC 6238-recommended minimum.
    private static readonly TimeSpan EmailCodeLifetime = TimeSpan.FromMinutes(10);

    public async Task<DataOutput<EnableTwoFactorAuthCommandOutput?>> HandleAsync(
        EnableTwoFactorAuthCommand command)
    {
        var output = DataOutput<EnableTwoFactorAuthCommandOutput?>.New;

        // AF-36c: validate input shape.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-36b: the caller must be a live person. See the remarks above for why this alone also
        // covers "the caller is a Google User".
        var person = await personReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ActingPersonId && !x.IsDeleted);

        if (person is null)
        {
            return output.WithError(TwoFactorMessages.NotEligible);
        }

        var wantsApp = command.Methods.Contains("App", StringComparer.OrdinalIgnoreCase);
        var wantsEmail = command.Methods.Contains("Email", StringComparer.OrdinalIgnoreCase);

        var existing = await twoFactorReader.Query()
            .FirstOrDefaultAsync(x => x.PersonId == person.Id);

        // AF-36a: an already-active configuration is not touched.
        if (existing is { IsActive: true })
        {
            return output.WithError(TwoFactorMessages.AlreadyActive);
        }

        // UC-36 step 2: create a pending row, or reuse the existing not-yet-confirmed one (AF-36d).
        var twoFactorAuth = existing;

        if (twoFactorAuth is null)
        {
            twoFactorAuth = new TwoFactorAuth { PersonId = person.Id, IsActive = false };

            var creation = await twoFactorWriter.CreateAsync(twoFactorAuth);

            if (!creation.Success)
            {
                return output.WithErrors(creation.Errors);
            }
        }

        var responseData = new EnableTwoFactorAuthCommandOutput();

        // AF-36d: re-initiation overwrites the pending configuration with the new selection — a
        // method dropped from a prior pending selection is cleared, not left active alongside the
        // new one, so UC-37 only ever asks for codes on the methods just (re)selected.
        if (!wantsApp)
        {
            twoFactorAuth.AppEnabled = false;
            twoFactorAuth.TotpSecretEncrypted = null;
        }

        if (!wantsEmail)
        {
            twoFactorAuth.EmailEnabled = false;

            var retirement = await RetireOutstandingCodesAsync(twoFactorAuth.Id);

            if (retirement is not null)
            {
                return output.WithErrors(retirement);
            }
        }

        // UC-36 step 3 (FR-2F-02): a fresh secret every time App is (re)selected, never returned
        // again once encrypted.
        if (wantsApp)
        {
            var secretKey = KeyGeneration.GenerateRandomKey(TotpSecretLengthInBytes);
            var base32Secret = Base32Encoding.ToString(secretKey);

            twoFactorAuth.AppEnabled = true;
            twoFactorAuth.TotpSecretEncrypted = totpSecretProtector.Protect(base32Secret);

            responseData.OtpAuthUri = BuildOtpAuthUri(person.Email, base32Secret);
        }

        // UC-36 step 4 (FR-2F-03): a fresh 6-digit code every time Email is (re)selected. Prior
        // outstanding codes are retired first, the same way ResendVerificationEmailCommandHandler
        // retires prior verification tokens, so only the code just mailed can ever be confirmed.
        if (wantsEmail)
        {
            var retirement = await RetireOutstandingCodesAsync(twoFactorAuth.Id);

            if (retirement is not null)
            {
                return output.WithErrors(retirement);
            }

            var code = CustomRandom.Text(new RandomStringOptions
            {
                Length = 6,
                IncludeDigits = true,
                IncludeLowercase = false,
                IncludeUppercase = false,
                IncludeSpecialCharacters = false
            });

            var codeHash = Hash.EncodeWithRandomSalt(code, out var salt);

            var emailCodeCreation = await emailCodeWriter.CreateAsync(new TwoFactorEmailCode
            {
                TwoFactorAuthId = twoFactorAuth.Id,
                CodeHash = codeHash,
                Salt = salt,
                ExpiresAt = DateTime.UtcNow.Add(EmailCodeLifetime),
                Used = false
            });

            if (!emailCodeCreation.Success)
            {
                return output.WithErrors(emailCodeCreation.Errors);
            }

            twoFactorAuth.EmailEnabled = true;

            // Delivery failures are not this endpoint's business to surface — the code is already
            // persisted by the time delivery is attempted, so a caller who receives nothing can call
            // this endpoint again, exactly as UC-15's resend does for verification email.
            await emailSender.SendAsync(person.Email, code);

            responseData.EmailCodeSent = true;
        }

        var update = await twoFactorWriter.UpdateAsync(twoFactorAuth);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        return output
            .WithData(responseData)
            .WithMessage(TwoFactorMessages.SetupInitiated);
    }

    /// <summary>
    ///     Marks every not-yet-used, not-yet-expired email code for this configuration as used, so a
    ///     freshly mailed code is the only one that can confirm setup — the same shape as
    ///     <see cref="ResendVerificationEmailCommandHandler" />'s token retirement.
    /// </summary>
    private async Task<IEnumerable<string>?> RetireOutstandingCodesAsync(long twoFactorAuthId)
    {
        var now = DateTime.UtcNow;

        var live = await emailCodeReader.Query()
            .Where(x => x.TwoFactorAuthId == twoFactorAuthId && !x.Used && x.ExpiresAt > now)
            .ToListAsync();

        foreach (var outstanding in live)
        {
            outstanding.Used = true;

            var update = await emailCodeWriter.UpdateAsync(outstanding);

            if (!update.Success)
            {
                return update.Errors;
            }
        }

        return null;
    }

    /// <summary>
    ///     Builds the <c>otpauth://</c> provisioning URI a caller's authenticator app scans (FR-2F-02).
    ///     Otp.NET generates the secret; the URI format itself is RFC-conventional and built here.
    /// </summary>
    private static string BuildOtpAuthUri(string email, string base32Secret) =>
        $"otpauth://totp/{Issuer}:{email}?secret={base32Secret}&issuer={Issuer}";
}
