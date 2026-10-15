namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Tells the previous address of a person that their account's email was changed (UC-08 step 4,
///     FR-PE-15). The concrete delivery is an infrastructure concern, as it is for
///     <see cref="IEmailVerificationSender" />.
/// </summary>
/// <remarks>
///     <para>
///         The notice goes to the address the change took away, because that is the one its owner
///         still reads if the change was not theirs: a token taken for a moment was enough to move an
///         account to an address its holder controls, and recover it from there. The notice is how
///         the owner finds out.
///     </para>
///     <para>
///         It is given only the previous address, never the new one, so it cannot disclose the new
///         one by construction: whoever made an unwanted change learns nothing from the notice, and
///         the owner is not handed a link they might be tricked into following. A failed delivery is
///         logged and does not fail the update, as with the other notices.
///     </para>
/// </remarks>
public interface IEmailChangeNoticeSender
{
    /// <param name="previousEmail">The address the account had before the change.</param>
    Task SendAsync(string previousEmail);
}
