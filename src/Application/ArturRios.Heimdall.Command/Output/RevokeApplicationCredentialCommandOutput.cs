using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>UC-55's answer. <see cref="Id" /> is the credential's <c>PublicId</c> and the audit target.</summary>
public class RevokeApplicationCredentialCommandOutput : CommandOutput
{
    public Guid Id { get; set; }

    public DateTime RevokedAt { get; set; }
}
