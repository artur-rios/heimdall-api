using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     The result of disabling two-factor authentication (UC-39). Nothing else to report — the
///     <c>TWO_FACTOR_AUTH</c> row and its recovery codes are gone, and the person's own identifier is
///     already implicit in the bearer token that authorized the request.
/// </summary>
public class DisableTwoFactorAuthCommandOutput : CommandOutput
{
    /// <summary>Always <c>true</c> when this output is returned — disabling only ever succeeds fully.</summary>
    public bool Disabled { get; set; }
}
