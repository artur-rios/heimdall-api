using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Output;

/// <summary>
///     Result of <see cref="Input.HardDeletePersonCommand" /> (UC-10). Reports the removed person's
///     <c>PublicId</c> and the totals of the applications and tokens removed with them — counted
///     regardless of their individual deletion state. Internal Ids never leave the data layer.
/// </summary>
public class HardDeletePersonCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the hard-deleted person.</summary>
    public Guid Id { get; set; }

    /// <summary>Total number of applications the person owned.</summary>
    public int DeletedApplicationCount { get; set; }

    /// <summary>Total number of password reset and email verification tokens issued for the person.</summary>
    public int DeletedTokenCount { get; set; }
}
