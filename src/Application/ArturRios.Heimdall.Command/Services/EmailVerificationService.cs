using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Util.Random;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Default <see cref="IEmailVerificationService" />: builds a random, time-limited
///     <see cref="EmailVerificationToken" />, persists it, then delegates delivery to the configured
///     <see cref="IEmailVerificationSender" />. A send failure does not undo the created person — the
///     persisted token can be re-sent later (UC-15).
/// </summary>
/// <remarks>
///     The token matches <see cref="PasswordResetService" />: 48 letters and digits drawn from
///     <c>RandomNumberGenerator</c> by <c>CustomRandom.Text</c>. As of ArturRios.Util 1.5.0 the
///     character-set flags are honoured for every character, so the token is alphanumeric — and
///     therefore URL-safe — throughout.
/// </remarks>
public class EmailVerificationService(
    IAsyncRepository<EmailVerificationToken> tokenWriter,
    IEmailVerificationSender sender,
    EmailVerificationOptions options)
    : IEmailVerificationService
{
    public async Task IssueAndSendAsync(Person person)
    {
        var token = CustomRandom.Text(new RandomStringOptions
        {
            Length = 48,
            IncludeLowercase = true,
            IncludeUppercase = true,
            IncludeDigits = true,
            IncludeSpecialCharacters = false
        });

        await tokenWriter.CreateAsync(new EmailVerificationToken
        {
            PersonId = person.Id,
            TokenHash = SingleUseTokenHash.Of(token),
            ExpiresAt = DateTime.UtcNow.Add(options.TokenLifetime),
            Used = false
        });

        await sender.SendAsync(person.Email, token);
    }
}
