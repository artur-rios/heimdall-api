using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Util.Random;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Default <see cref="IPasswordResetService" /> (UC-12 steps 3 and 4, FR-PR-02): builds a
///     random, time-limited <see cref="PasswordResetToken" />, persists it, then delegates delivery
///     to the configured <see cref="IPasswordResetSender" />.
/// </summary>
/// <remarks>
///     <para>
///         The options ask for letters and digits only, matching <see cref="EmailVerificationService" />.
///         As of ArturRios.Util 1.5.0 <c>CustomRandom.Text</c> honours those flags for every character,
///         so the token is alphanumeric — and therefore URL-safe — throughout. The sender still escapes
///         the token before putting it in a link; nothing depends on that escaping being a no-op.
///     </para>
///     <para>
///         Secrecy comes from the source and the length: <c>CustomRandom.Text</c> draws from
///         <c>RandomNumberGenerator</c>, and 48 alphanumeric characters are far past guessing range
///         within the token's one-hour life.
///     </para>
/// </remarks>
public class PasswordResetService(
    IAsyncRepository<PasswordResetToken> tokenWriter,
    IPasswordResetSender sender,
    PasswordResetOptions options)
    : IPasswordResetService
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

        await tokenWriter.CreateAsync(new PasswordResetToken
        {
            PersonId = person.Id,
            Token = token,
            ExpiresAt = DateTime.UtcNow.Add(options.TokenLifetime),
            Used = false
        });

        await sender.SendAsync(person.Email, token);
    }
}
