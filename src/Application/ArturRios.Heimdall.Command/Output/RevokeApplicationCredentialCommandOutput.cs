using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>UC-55's answer. <see cref="Id" /> is the credential's <c>PublicId</c> and the audit target.</summary>
public class RevokeApplicationCredentialCommandOutput : CommandOutput
{
    public Guid Id { get; init; }

    public DateTime RevokedAt { get; init; }
}
