using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     The result of confirming two-factor authentication setup (UC-37). The ten recovery codes are
///     returned in plaintext this one time only — they are stored thereafter only as hashes and
///     cannot be retrieved again (only regenerated, via UC-40).
/// </summary>
public class ConfirmTwoFactorAuthCommandOutput : CommandOutput
{
    /// <summary>Always <c>true</c> when this output is returned — confirmation only ever succeeds fully.</summary>
    public bool Enabled { get; set; }

    /// <summary>The ten freshly generated recovery codes, in plaintext (FR-2F-05).</summary>
    public List<string> RecoveryCodes { get; set; } = [];
}
