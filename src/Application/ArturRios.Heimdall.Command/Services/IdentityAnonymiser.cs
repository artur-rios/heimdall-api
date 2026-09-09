using ArturRios.Heimdall.Domain.Entities;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Overwrites every value on an identity that identifies a natural person, leaving the row and
///     its foreign keys intact (NFR-20). The operation both laws recognise as putting data outside
///     their scope: GDPR Recital 26 and LGPD Art. 12 exempt anonymised data, so an anonymised row is
///     no longer personal data being retained.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why overwrite rather than delete the row.</b> NFR-07 requires every foreign key in the
///         schema to still resolve after a deletion, and removing the row breaks that — the audit
///         trail, the scope membership and ownership rows, and the applications an owner held all
///         point at it. Overwriting in place satisfies the erasure obligation without touching a
///         single relationship. Hard deletion remains available as UC-10 for callers that want the
///         row gone.
///     </para>
///     <para>
///         <b>Why the placeholders are unique per row.</b> `GOOGLE_USER` carries unconditional
///         unique indexes on <c>(scope_id, google_id)</c> and <c>(scope_id, LOWER(email))</c>, so a
///         constant placeholder would collide the moment a second Google User in the same scope was
///         anonymised — turning an erasure into a failed write. Deriving them from
///         <c>PublicId</c> keeps them unique by construction. `PERSON`'s equivalent indexes are
///         filtered on <c>is_deleted = false</c> and so never see these rows, but the same
///         derivation is used there rather than a constant, because a rule that holds only while an
///         index keeps its current filter is a rule waiting to break.
///     </para>
///     <para>
///         <b>What remains, and why that is still anonymisation.</b> The row keeps its
///         <c>PublicId</c> — a random GUID that the audit trail and the scope join rows reference.
///         Once the identifying columns are gone nothing in this system maps it back to a person, so
///         within Heimdall the row relates to no identifiable natural person. A client system that
///         stored the <c>PublicId</c> alongside its own copy of the name is a separate matter, and
///         one for the tenant that controls that copy.
///     </para>
/// </remarks>
public static class IdentityAnonymiser
{
    /// <summary>Display name left in place of the real one.</summary>
    public const string AnonymisedName = "Anonymised";

    /// <summary>
    ///     Domain the placeholder addresses are built under. <c>.invalid</c> is reserved by RFC 2606
    ///     and guaranteed never to resolve, so a placeholder can never become a deliverable address
    ///     — an erased person must not be reachable by an email this system sends.
    /// </summary>
    public const string AnonymisedEmailDomain = "anonymised.invalid";

    /// <summary>
    ///     Overwrites the identifying values on <paramref name="person" /> and stamps
    ///     <paramref name="at" />. Does not touch <c>IsDeleted</c>, the role, the scope, or any
    ///     foreign key.
    /// </summary>
    public static void Anonymise(Person person, DateTime at)
    {
        person.Name = AnonymisedName;
        person.Email = PlaceholderEmail(person.PublicId);

        // Zeroed rather than left alone: a hash and its salt are the material an offline attack
        // works on, and keeping them for a record nobody can log into serves nothing. Both columns
        // are NOT NULL, so they are emptied rather than nulled.
        person.PasswordHash = [];
        person.Salt = [];

        // Behavioural data: how often this person mistyped their password, and whether they were
        // locked out, describes them as surely as their name does.
        person.FailedLoginAttempts = 0;
        person.LockedOutUntil = null;

        // An anonymised address is not a verified one, and leaving the flag set would let a restored
        // record skip verification for an address that no longer belongs to anybody.
        person.EmailVerified = false;

        person.AnonymisedAt = at;
        person.UpdatedAt = at;
    }

    /// <summary>
    ///     Overwrites the identifying values on <paramref name="googleUser" /> and stamps
    ///     <paramref name="at" />.
    /// </summary>
    public static void Anonymise(GoogleUser googleUser, DateTime at)
    {
        googleUser.Name = AnonymisedName;
        googleUser.Email = PlaceholderEmail(googleUser.PublicId);

        // Google's 'sub' is the strongest identifier on this row: it is stable across Google's whole
        // estate, so leaving it would let anyone holding it re-identify the person from outside this
        // system entirely. It carries a unique index, hence the derived value rather than null.
        googleUser.GoogleId = $"anonymised-{googleUser.PublicId:N}";

        // A profile picture is a photograph of a person.
        googleUser.ProfilePictureUrl = null;

        googleUser.EmailVerified = false;

        googleUser.AnonymisedAt = at;
        googleUser.UpdatedAt = at;
    }

    /// <summary>
    ///     The placeholder address for an identity, unique to it and never deliverable.
    /// </summary>
    public static string PlaceholderEmail(Guid publicId) => $"anonymised-{publicId:N}@{AnonymisedEmailDomain}";
}
