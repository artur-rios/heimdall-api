using ArturRios.Heimdall.Command.Services;
using ArturRios.Messaging.Email;

namespace ArturRios.Heimdall.WebApi.Email;

/// <summary>
///     Delivers the scope owner invitation notices through Mailgun (UC-21, UC-60, UC-61). A failure is logged
///     by <see cref="MailgunSender" /> and never propagated: the invitation or its answer is already stored.
/// </summary>
public class MailgunScopeInvitationNoticeSender(
    IEmailService emailService,
    ILogger<MailgunScopeInvitationNoticeSender> logger)
    : MailgunSender(emailService, logger), IScopeInvitationNoticeSender
{
    protected override string Purpose => "scope invitation notice";

    public Task SendInvitationAsync(string inviteeEmail, string inviterName, string scopeName, DateTime expiresAt) =>
        DeliverAsync(inviteeEmail, ScopeInvitationNoticeText.InvitationSubject,
            ScopeInvitationNoticeText.Invitation(inviterName, scopeName, expiresAt));

    public Task SendAnswerAsync(string inviterEmail, string inviteeName, string scopeName, bool accepted) =>
        DeliverAsync(inviterEmail, ScopeInvitationNoticeText.AnswerSubject,
            ScopeInvitationNoticeText.Answer(inviteeName, scopeName, accepted));
}
