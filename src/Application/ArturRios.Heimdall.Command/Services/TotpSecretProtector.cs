using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Default <see cref="ITotpSecretProtector" />, wrapping ASP.NET Core's Data Protection API
///     (UC-36 step 3, FR-2F-02). Registered from <c>Startup.AddDependencies</c>, which also adds
///     <c>AddDataProtection()</c> to the container this protector's <see cref="IDataProtector" /> is
///     resolved from.
/// </summary>
/// <remarks>
///     A single, fixed purpose string scopes the protector to this one use — Data Protection derives
///     a different key for every distinct purpose, so a secret protected here cannot be unprotected
///     by any other component that also happens to call <c>CreateProtector</c>.
/// </remarks>
public class TotpSecretProtector : ITotpSecretProtector
{
    private const string Purpose = "ArturRios.Heimdall.TwoFactorAuth.TotpSecret.v1";

    private readonly IDataProtector _protector;

    public TotpSecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public byte[] Protect(string base32Secret) =>
        _protector.Protect(Encoding.UTF8.GetBytes(base32Secret));

    public string Unprotect(byte[] protectedSecret) =>
        Encoding.UTF8.GetString(_protector.Unprotect(protectedSecret));
}
