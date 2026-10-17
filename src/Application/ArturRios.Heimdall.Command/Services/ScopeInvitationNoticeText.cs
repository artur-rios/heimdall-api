using System.Globalization;
using System.Text;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     The exact wording of the scope owner invitation notices (UC-21, UC-60, UC-61), kept beside the port so
///     the logging and Mailgun senders cannot drift from the sentence the specification fixes.
/// </summary>
/// <remarks>
///     The names come from records administrators control — a Scope Admin can rename themselves — and reach
///     another person's mailbox, so each is folded onto one line (<see cref="OneLine" />). Nothing a caller
///     controls is put in a subject.
/// </remarks>
public static class ScopeInvitationNoticeText
{
    public const string InvitationSubject = "You were invited to co-own a scope";

    public const string AnswerSubject = "Your scope invitation was answered";

    /// <summary>The invitee's notice. No token and no link (TH-41).</summary>
    public static string Invitation(string inviterName, string scopeName, DateTime expiresAt) =>
        $"{OneLine(inviterName)} invited you to co-own the scope {OneLine(scopeName)}. " +
        $"Sign in to Heimdall to accept or decline. The invitation expires on {Format(expiresAt)}.";

    /// <summary>The inviter's notice once the invitee answered.</summary>
    public static string Answer(string inviteeName, string scopeName, bool accepted) =>
        $"{OneLine(inviteeName)} {(accepted ? "accepted" : "declined")} your invitation to co-own the scope " +
        $"{OneLine(scopeName)}.";

    /// <summary>
    ///     <paramref name="value" /> with every control character and every run of whitespace, line breaks and
    ///     Unicode line separators included, turned into one space, and none at either end.
    /// </summary>
    public static string OneLine(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;

                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string Format(DateTime at) => at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
