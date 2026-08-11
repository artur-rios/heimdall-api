using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     The result of regenerating recovery codes (UC-40). The ten new codes are returned in
///     plaintext this one time only — like <see cref="ConfirmTwoFactorAuthCommandOutput" />'s, they
///     are stored thereafter only as hashes and cannot be retrieved again, only regenerated once more.
/// </summary>
public class RegenerateRecoveryCodesCommandOutput : CommandOutput
{
    /// <summary>The ten freshly generated recovery codes, in plaintext (FR-2F-12).</summary>
    public List<string> RecoveryCodes { get; set; } = [];
}
