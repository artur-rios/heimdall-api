using ArturRios.Heimdall.Command.Output;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     The context Art. 15 requires an export to carry alongside the data: who else receives it,
///     how long each category is kept, and what is held but not reproduced.
/// </summary>
/// <remarks>
///     <para>
///         This duplicates the Data Protection Document and the Data Retention Schedule, which is a
///         real cost and is accepted deliberately: the subject receives one document, and a copy
///         that told them to go and read a repository would not be the "concise, transparent,
///         intelligible" form Art. 12(1) asks for.
///     </para>
///     <para>
///         The duplication is guarded rather than trusted. <c>DataExportDisclosureTests</c> asserts
///         that every recipient named here appears in the record of processing, so the two cannot
///         drift apart silently — which is the only failure mode duplication of this kind has.
///     </para>
/// </remarks>
public static class DataExportDisclosure
{
    /// <summary>Most audit entries a single export reproduces.</summary>
    /// <remarks>
    ///     A bound is needed — an old, busy account could otherwise produce a response measured in
    ///     megabytes — but a silently truncated copy is not the copy Art. 15 asks for, so reaching
    ///     it is reported on the document itself.
    /// </remarks>
    public const int MaximumAuditEntries = 5000;

    /// <summary>Who else the subject's data reaches (GDPR Art. 15(1)(c), LGPD Art. 18 VII).</summary>
    public static IEnumerable<DataExportRecipient> Recipients =>
    [
        new()
        {
            Name = "Mailgun",
            Purpose = "Delivering verification, password reset and two-factor emails",
            Location = "United States"
        },
        new()
        {
            Name = "The organisation whose system this account belongs to",
            Purpose = "Administering the identities within its own scope",
            Location = "Varies by tenant"
        },
        new()
        {
            Name = "Hosting provider",
            Purpose = "Storing the database",
            Location = "Brazil"
        }
    ];

    /// <summary>
    ///     Where data that did not come from the subject came from (GDPR Art. 14(2)(f) and Art.
    ///     15(1)(g), LGPD Art. 9).
    /// </summary>
    /// <remarks>
    ///     Google was listed as a <em>recipient</em> until the transfer assessment was corrected,
    ///     and it is not one: the ID token is validated offline against cached public certificates
    ///     and nothing about the person is ever sent to Google. It belongs here, where the direction
    ///     is right — and telling the subject where their data came from is an obligation in its own
    ///     right when it did not come from them.
    /// </remarks>
    public static IEnumerable<DataExportSource> Sources =>
    [
        new()
        {
            Name = "Google",
            Provides =
                "Your name, email address, profile picture and Google account identifier, if you "
                + "signed in with Google. They come from the token Google issues when you sign in.",
            DataSentThere =
                "Nothing. The token is checked against Google's published certificates on this "
                + "server; it is never sent to Google."
        }
    ];

    /// <summary>How long each category is kept (GDPR Art. 15(1)(d)).</summary>
    public static IEnumerable<DataExportRetention> Retention =>
    [
        new() { Category = "Account", Period = "While the account exists" },
        new() { Category = "Account, after you ask to be erased", Period = "Anonymised within 30 days" },
        new()
        {
            Category = "Account, after an administrator deletes it",
            Period = "Anonymised after 90 days, so a mistake can be undone"
        },
        new() { Category = "Password reset and verification links", Period = "A week after they expire" },
        new()
        {
            Category = "Records of actions on the account",
            Period = "18 months, then stripped of anything identifying you"
        },
        new() { Category = "Application logs", Period = "12 months" }
    ];

    /// <summary>
    ///     Values held about the subject that this document names but does not reproduce.
    /// </summary>
    /// <remarks>
    ///     Art. 15(4): the right to a copy must not adversely affect the rights of others — and
    ///     reproducing a password hash or a TOTP secret would hand whoever holds the token the
    ///     material to attack the account offline, which adversely affects the subject most of all.
    ///     Naming them is the compromise: the subject learns what exists without the export becoming
    ///     the best thing to steal.
    /// </remarks>
    public static IEnumerable<DataExportWithheld> Withheld =>
    [
        new()
        {
            Value = "Password hash and salt",
            Reason = "Reproducing them would let anyone holding this document attack the password offline. "
                     + "The password itself is not stored and cannot be recovered from what is."
        },
        new()
        {
            Value = "Two-factor secret",
            Reason = "Encrypted at rest and never returned after the response that first generated it. "
                     + "Reproducing it would defeat the second factor entirely."
        },
        new()
        {
            Value = "Two-factor recovery codes",
            Reason = "Stored only as hashes, so the codes themselves no longer exist to reproduce."
        },
        new()
        {
            Value = "Password reset and email verification tokens",
            Reason = "Stored only as digests, and a live one would let this document be used to take over the account."
        }
    ];
}
