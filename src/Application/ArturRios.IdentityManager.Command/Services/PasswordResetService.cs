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
///     The token is drawn from letters and digits only. Its secrecy comes from its length, not its
///     alphabet, and it has to survive a round trip through a URL in an email — special characters
///     buy nothing and risk being mangled on the way.
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
