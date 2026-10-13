using ArturRios.Util.Random;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     A fresh single-use token: 48 letters and digits from a CSPRNG, URL-safe — the same shape
///     <see cref="EmailVerificationService" /> and <see cref="PasswordResetService" /> issue. Stored only
///     through <see cref="SingleUseTokenHash" />.
/// </summary>
public static class SingleUseToken
{
    public static string New() => CustomRandom.Text(new RandomStringOptions
    {
        Length = 48,
        IncludeLowercase = true,
        IncludeUppercase = true,
        IncludeDigits = true,
        IncludeSpecialCharacters = false
    });
}
