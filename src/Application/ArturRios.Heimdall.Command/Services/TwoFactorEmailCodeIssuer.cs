using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Util.Hashing;
using ArturRios.Util.Random;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Services;

/// <inheritdoc cref="ITwoFactorEmailCodeIssuer" />
public class TwoFactorEmailCodeIssuer(
    IAsyncReadOnlyRepository<TwoFactorEmailCode> emailCodeReader,
    IAsyncRepository<TwoFactorEmailCode> emailCodeWriter,
    ITwoFactorEmailSender emailSender) : ITwoFactorEmailCodeIssuer
{
    private const int CodeLength = 6;

    public async Task<IEnumerable<string>?> ReissueAsync(TwoFactorAuth twoFactorAuth, string email)
    {
        var retirement = await RetireOutstandingAsync(twoFactorAuth.Id);

        if (retirement is not null)
        {
            return retirement;
        }

        var code = CustomRandom.Text(new RandomStringOptions
        {
            Length = CodeLength,
            IncludeDigits = true,
            IncludeLowercase = false,
            IncludeUppercase = false,
            IncludeSpecialCharacters = false
        });

        var codeHash = Hash.EncodeWithRandomSalt(code, out var salt);

        var creation = await emailCodeWriter.CreateAsync(new TwoFactorEmailCode
        {
            TwoFactorAuthId = twoFactorAuth.Id,
            CodeHash = codeHash,
            Salt = salt,
            ExpiresAt = DateTime.UtcNow.Add(TwoFactorLifetimes.EmailCode),
            Used = false
        });

        if (!creation.Success)
        {
            return creation.Errors;
        }

        // Delivery is attempted only after the code is persisted, and its outcome is deliberately
        // not reported. A caller who receives nothing asks for another (UC-46) or logs in again;
        // telling them the send failed would say something about the address, and telling them it
        // succeeded would not be true of the inbox anyway.
        await emailSender.SendAsync(email, code);

        return null;
    }

    public async Task<IEnumerable<string>?> RetireOutstandingAsync(long twoFactorAuthId)
    {
        var now = DateTime.UtcNow;

        var live = await emailCodeReader.Query()
            .Where(x => x.TwoFactorAuthId == twoFactorAuthId && !x.Used && x.ExpiresAt > now)
            .ToListAsync();

        foreach (var outstanding in live)
        {
            outstanding.Used = true;

            var retirement = await emailCodeWriter.UpdateAsync(outstanding);

            if (!retirement.Success)
            {
                return retirement.Errors;
            }
        }

        return null;
    }
}
