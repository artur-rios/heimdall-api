namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Encrypts and decrypts a TOTP secret at rest (UC-36 step 3, FR-2F-02). The plaintext secret is
///     shown to the person only once, at initiation — everything persisted afterward goes through
///     this protector, so the database never holds it unencrypted.
/// </summary>
public interface ITotpSecretProtector
{
    /// <summary>Encrypts a base32-encoded TOTP secret for storage.</summary>
    byte[] Protect(string base32Secret);

    /// <summary>Decrypts a previously protected TOTP secret back to its base32 form.</summary>
    string Unprotect(byte[] protectedSecret);
}
