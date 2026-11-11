using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     UC-53's answer. <see cref="Secret" /> appears here and nowhere else, ever; <see cref="Id" />
///     is the credential's <c>PublicId</c> and the audit target.
/// </summary>
public class IssueApplicationCredentialCommandOutput : CommandOutput
{
    public Guid Id { get; init; }

    public string Secret { get; init; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? ExpiresAt { get; init; }
}
