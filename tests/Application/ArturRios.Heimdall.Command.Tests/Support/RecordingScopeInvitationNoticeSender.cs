using ArturRios.Heimdall.Command.Services;

namespace ArturRios.Heimdall.Command.Tests.Support;

/// <summary>Records every invitation notice it is asked for, and sends nothing.</summary>
public sealed class RecordingScopeInvitationNoticeSender : IScopeInvitationNoticeSender
{
    public List<(string To, string InviterName, string ScopeName, DateTime ExpiresAt)> Invitations { get; } = [];

    public List<(string To, string InviteeName, string ScopeName, bool Accepted)> Answers { get; } = [];

    public Task SendInvitationAsync(string inviteeEmail, string inviterName, string scopeName, DateTime expiresAt)
    {
        Invitations.Add((inviteeEmail, inviterName, scopeName, expiresAt));

        return Task.CompletedTask;
    }

    public Task SendAnswerAsync(string inviterEmail, string inviteeName, string scopeName, bool accepted)
    {
        Answers.Add((inviterEmail, inviteeName, scopeName, accepted));

        return Task.CompletedTask;
    }
}
