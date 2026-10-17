namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Tells the people a scope owner invitation concerns what happened to it: the invitee that they were
///     invited (UC-21), the inviter that it was accepted or declined (UC-60, UC-61). The concrete delivery
///     is an infrastructure concern, as it is for <see cref="IEmailChangeNoticeSender" />.
/// </summary>
/// <remarks>
///     The wording is <see cref="ScopeInvitationNoticeText" />'s. Neither notice carries a token or a link:
///     answering is an action the invitee takes signed in as themselves, so the mail holds nothing worth
///     intercepting or forwarding (Threat Model TH-41). A failed delivery is logged and never fails the
///     request, as with the other notices.
/// </remarks>
public interface IScopeInvitationNoticeSender
{
    /// <param name="inviteeEmail">The invited administrator's address.</param>
    /// <param name="inviterName">Who invited them.</param>
    /// <param name="scopeName">The scope they are invited to co-own.</param>
    /// <param name="expiresAt">When the invitation stops being acceptable.</param>
    Task SendInvitationAsync(string inviteeEmail, string inviterName, string scopeName, DateTime expiresAt);

    /// <param name="inviterEmail">The address of the administrator who invited.</param>
    /// <param name="inviteeName">Who answered.</param>
    /// <param name="scopeName">The scope the invitation was for.</param>
    /// <param name="accepted"><c>true</c> for an acceptance, <c>false</c> for a decline.</param>
    Task SendAnswerAsync(string inviterEmail, string inviteeName, string scopeName, bool accepted);
}
