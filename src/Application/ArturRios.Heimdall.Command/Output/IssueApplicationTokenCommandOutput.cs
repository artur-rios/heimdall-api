using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     UC-56's answer. <see cref="Id" /> is the application's <c>PublicId</c> — the one the caller
///     sent — and the audit target.
/// </summary>
public class IssueApplicationTokenCommandOutput : CommandOutput
{
    public Guid Id { get; set; }

    /// <summary>The application token, valid until <see cref="ExpiresAt" />.</summary>
    public string Token { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }
}
