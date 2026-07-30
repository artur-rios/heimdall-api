using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.Util.Random;

namespace ArturRios.IdentityManager.Command.Services;

/// <summary>
///     Default <see cref="IPasswordResetService" /> (UC-12 steps 3 and 4, FR-PR-02): builds a
///     random, time-limited <see cref="PasswordResetToken" />, persists it, then delegates delivery
///     to the configured <see cref="IPasswordResetSender" />.
/// </summary>
/// <remarks>
///     <para>
///         The options ask for letters and digits only, matching <see cref="EmailVerificationService" />.
///         Note that <c>CustomRandom.Text</c> honours those flags only for the first character of
///         each requested class and pads the rest from its full alphabet, so the token does contain
///         special characters in practice. That is harmless here — it raises entropy, not lowers it,
///         and the sender escapes the token before putting it in a link — but it means nothing may
///         assume the token is URL-safe on its own.
///     </para>
///     <para>
///         Secrecy comes from the length: 48 characters is far past guessing range within the
///         token's one-hour life.
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
