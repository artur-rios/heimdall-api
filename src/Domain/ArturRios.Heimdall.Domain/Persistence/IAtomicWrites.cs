using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;

namespace ArturRios.Heimdall.Domain.Persistence;

/// <summary>
///     The writes that concurrent requests compete for: spending something single-use, and charging
///     a bounded budget. Each is one conditional <c>UPDATE … WHERE</c> evaluated by the database, so of
///     any number of simultaneous requests exactly as many succeed as the condition allows — one, for
///     a single-use item.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> The repositories write whole rows: read, change in memory, save.
///         Two requests that read the same row before either saves both see "unused", "under the
///         cap" or "not yet redeemed", and both act on it — one recovery code redeemed twice, one
///         challenge traded for two tokens, ten wrong passwords counted as one. Moving the check into
///         the <c>WHERE</c> clause of the write that spends it closes that window, because the
///         database applies the two together.
///     </para>
///     <para>
///         <b>Charge first, then check.</b> Every budget here — login attempts (FR-AU-09), second
///         factors at sign-in (FR-AU-12), re-authentications under a bearer token (FR-AU-13), guesses
///         at an email code (FR-2F-13) or at a challenge, guesses at the password of a pending
///         registration (FR-SR-09), reissues of a challenge's code (FR-2F-16) — is reserved before the
///         expensive or secret comparison runs, not counted after it fails.
///         Counting afterwards let a burst of parallel guesses all pass the "under the cap" check
///         before any of them was counted; reserving first means no more comparisons can ever run
///         than the cap allows.
///     </para>
///     <para>
///         <b>Writes beside a revocation.</b> An application credential is revoked by one request
///         (UC-55) while another may be using it (UC-56). A whole-row save of either request's copy
///         would write <c>revoked_at</c> back as that request read it, and silently undo or overwrite
///         the revocation. So both the revocation and the use stamp are targeted, conditional writes
///         here: the first revocation stands, and a use stamp only lands while the credential is
///         still live.
///     </para>
///     <para>
///         <b>Writes of plain details</b> (#205). A person's name, or a scope's name and description, races
///         nothing single-use, but a whole-row save of them would still write every other column back as
///         the request read it: a restriction or an erasure request made meanwhile, a scope deleted
///         meanwhile, or the scope settings that have endpoints of their own. So those are targeted too,
///         writing their own columns and <c>UpdatedAt</c> only, under the condition the caller read — the
///         row not deleted.
///     </para>
///     <para>
///         Every method takes the entity the caller already holds and, when the write succeeds,
///         brings that instance's changed properties in line with what was written, so the caller
///         and anything it saves later see the same values the database does.
///     </para>
/// </remarks>
public interface IAtomicWrites
{
    /// <summary>
    ///     FR-AU-09: reserves one login attempt — increments the failure counter, but only while the
    ///     account is not locked out at <paramref name="now" /> and the counter is below
    ///     <paramref name="maxAttempts" />. <c>false</c> means no attempt may be made.
    /// </summary>
    /// <remarks>
    ///     The attempt that brings the counter to <paramref name="maxAttempts" /> also locks the
    ///     account until <paramref name="lockedOutUntil" /> and resets the counter, in the same
    ///     write. The lock therefore does not depend on the caller surviving to compare the
    ///     password: a request that reserves the last attempt and then fails leaves an ordinary
    ///     lock that expires on its own. A row already at or past the limit with no lockout date is
    ///     admitted and locked the same way, so it cannot stay stuck. On success the instance's
    ///     counter and lockout are set to match, from the value this request read, which under
    ///     concurrent attempts can trail the stored count.
    /// </remarks>
    Task<bool> TryReserveLoginAttemptAsync(Person person, int maxAttempts, DateTime now, DateTime lockedOutUntil);

    /// <summary>
    ///     FR-SR-09: reserves one guess at the sign-up password of a pending registration, while it has
    ///     had fewer than <paramref name="maxGuesses" />. <c>false</c> means no comparison may be made.
    /// </summary>
    /// <remarks>
    ///     On success the instance's <c>FailedAttempts</c> is set to the value this request read plus one,
    ///     which under concurrent guesses can trail the stored count. A caller that needs the stored count
    ///     (to tell whether this was the last guess) must ask the database for it.
    /// </remarks>
    Task<bool> TryChargePendingRegistrationGuessAsync(PendingRegistration pending, int maxGuesses);

    /// <summary>FR-AU-09: after a correct password, clears the counter and any lockout.</summary>
    Task ClearLoginAttemptsAsync(Person person);

    /// <summary>
    ///     FR-AU-12: reserves one second-factor verification at sign-in — increments the account's
    ///     second-factor failure counter, but only while the second factor is not locked out at
    ///     <paramref name="now" /> and the counter is below <paramref name="maxAttempts" />.
    ///     <c>false</c> means the second factor is locked and no factor may be accepted.
    /// </summary>
    /// <param name="person">The account the verification is charged to.</param>
    /// <param name="maxAttempts">The failures that lock the second factor.</param>
    /// <param name="now">The time the lock is compared against.</param>
    /// <param name="lockedOutUntil">
    ///     When each lock ends, by how many the account has had before it: the first entry for the
    ///     first lock, the second for the next, and the last for every lock from then on. Must not
    ///     be empty.
    /// </param>
    /// <remarks>
    ///     As <see cref="TryReserveLoginAttemptAsync" /> does, the reservation that brings the counter
    ///     to <paramref name="maxAttempts" /> locks in the same write — choosing the end by the
    ///     stored lockout count, which it then increments — and resets the counter, so the lock does
    ///     not depend on the caller surviving to compare the factor. A row at or past the limit with
    ///     no live lock — none, or one that has run out — is admitted and locked the same way. A
    ///     refused reservation still writes the row, unchanged, so it costs what an admitted one does.
    /// </remarks>
    Task<bool> TryReserveSecondFactorAttemptAsync(
        Person person, int maxAttempts, DateTime now, IReadOnlyList<DateTime> lockedOutUntil);

    /// <summary>
    ///     FR-AU-12: after a second factor checks out at sign-in, clears the failure counter, any lock,
    ///     and the lockout count that sets how long the next lock lasts.
    /// </summary>
    Task ClearSecondFactorAttemptsAsync(Person person);

    /// <summary>
    ///     FR-AU-13: reserves one re-authentication — a password or a factor checked under a bearer
    ///     token — while the account's re-authentication is not paused at <paramref name="now" />.
    ///     <c>false</c> means it is paused and no credential may be accepted.
    /// </summary>
    /// <param name="person">The account the re-authentication is charged to.</param>
    /// <param name="maxFailures">The failures within one window that pause re-authentication.</param>
    /// <param name="now">The time the pause is compared against, and a new window starts at.</param>
    /// <param name="windowStartedBy">
    ///     The earliest a window may have started and still be open; one that started at or before
    ///     it has run out, and the reservation starts a new one.
    /// </param>
    /// <param name="pausedUntil">When the pause the reservation may set ends.</param>
    /// <remarks>
    ///     The reservation counts as a failure until <see cref="ClearReauthenticationAttemptsAsync" />
    ///     says otherwise. The one that brings the window's count to <paramref name="maxFailures" />
    ///     pauses re-authentication until <paramref name="pausedUntil" /> and closes the window, in the
    ///     same write. Nothing here touches the login lockout or the second-factor budget. A refused
    ///     reservation still writes the row, unchanged, so it costs what an admitted one does.
    /// </remarks>
    Task<bool> TryReserveReauthenticationAsync(
        Person person, int maxFailures, DateTime now, DateTime windowStartedBy, DateTime pausedUntil);

    /// <summary>
    ///     FR-AU-13: after a re-authentication succeeds, clears the window's count and any pause.
    /// </summary>
    Task ClearReauthenticationAttemptsAsync(Person person);

    /// <summary>
    ///     FR-PE-16: reserves one account email the person's own request would send — while none was
    ///     reserved after <paramref name="lastSentBefore" /> (the cooldown) and fewer than
    ///     <paramref name="maxPerWindow" /> were in a window still open at <paramref name="now" />.
    /// </summary>
    /// <param name="person">The account the email is charged to.</param>
    /// <param name="maxPerWindow">The sends one window admits.</param>
    /// <param name="now">The time the window is compared against, and the send is stamped with.</param>
    /// <param name="lastSentBefore">
    ///     The latest the previous send may have been reserved and still be out of the cooldown:
    ///     <paramref name="now" /> minus the cooldown.
    /// </param>
    /// <param name="windowEndsAt">When the window this send opens ends, if it opens one.</param>
    /// <remarks>
    ///     A send in an ended window, or the first ever, opens a new window with itself as the first
    ///     send. On success the instance's three columns are set to what was written. A refusal reads
    ///     the row back only to say which bound refused it; when both do, the daily one is reported.
    /// </remarks>
    Task<AccountEmailReservation> TryReserveAccountEmailAsync(
        Person person, int maxPerWindow, DateTime now, DateTime lastSentBefore, DateTime windowEndsAt);

    /// <summary>
    ///     FR-2F-10: makes <paramref name="challengeId" /> the configuration's one outstanding
    ///     challenge, with all of its reissues (FR-2F-16) and guesses available.
    /// </summary>
    Task StartChallengeAsync(TwoFactorAuth configuration, Guid challengeId);

    /// <summary>
    ///     Reserves one guess at the outstanding challenge <paramref name="challengeId" />, while it is
    ///     still outstanding and has had fewer than <paramref name="maxGuesses" />.
    /// </summary>
    Task<bool> TryChargeChallengeGuessAsync(TwoFactorAuth configuration, Guid challengeId, int maxGuesses);

    /// <summary>
    ///     After a wrong guess, retires the challenge — clears it, so its token is refused from then on
    ///     — if it has had <paramref name="maxGuesses" />.
    /// </summary>
    Task ExpireChallengeIfExhaustedAsync(TwoFactorAuth configuration, Guid challengeId, int maxGuesses);

    /// <summary>FR-2F-10: redeems the outstanding challenge <paramref name="challengeId" />. Once.</summary>
    Task<bool> TryRedeemChallengeAsync(TwoFactorAuth configuration, Guid challengeId);

    /// <summary>
    ///     FR-2F-16: reserves one reissue of the outstanding challenge's email code, while it has had
    ///     fewer than <paramref name="maxReissues" />.
    /// </summary>
    Task<bool> TryChargeEmailCodeReissueAsync(TwoFactorAuth configuration, Guid challengeId, int maxReissues);

    /// <summary>
    ///     FR-2F-14: records <paramref name="timeStep" /> as the last accepted TOTP step, if it is later
    ///     than the one recorded. <c>false</c> means the code was already used, or a later one has been.
    /// </summary>
    Task<bool> TryAdvanceTotpStepAsync(TwoFactorAuth configuration, long timeStep, DateTime now);

    /// <summary>
    ///     FR-2F-13: reserves one guess at an email code that is unused, unexpired at
    ///     <paramref name="now" />, and has had fewer than <paramref name="maxGuesses" />.
    /// </summary>
    Task<bool> TryChargeEmailCodeGuessAsync(TwoFactorEmailCode code, int maxGuesses, DateTime now);

    /// <summary>FR-2F-13: after a wrong guess, retires the code if it has had <paramref name="maxGuesses" />.</summary>
    Task RetireEmailCodeIfExhaustedAsync(TwoFactorEmailCode code, int maxGuesses);

    /// <summary>Spends an email code. Once.</summary>
    Task<bool> TryConsumeEmailCodeAsync(TwoFactorEmailCode code);

    /// <summary>Spends a recovery code (UC-38, UC-40). Once.</summary>
    Task<bool> TryConsumeRecoveryCodeAsync(TwoFactorRecoveryCode code, DateTime usedAt);

    /// <summary>UC-13: spends a password reset token that is unexpired at <paramref name="now" />. Once.</summary>
    Task<bool> TryConsumePasswordResetTokenAsync(PasswordResetToken token, DateTime now);

    /// <summary>UC-14: spends an email verification token that is unexpired at <paramref name="now" />. Once.</summary>
    Task<bool> TryConsumeEmailVerificationTokenAsync(EmailVerificationToken token, DateTime now);

    /// <summary>
    ///     UC-13 (FR-PR-08, FR-AU-11): writes the reset password — <paramref name="passwordHash" />,
    ///     <paramref name="salt" />, a fresh <see cref="Person.SecurityStamp" /> and <c>UpdatedAt</c>,
    ///     and no other column — while the person still holds the address they were read with and is
    ///     neither logically deleted, under a restriction (NFR-24), nor anonymised. <c>false</c> means
    ///     one of those changed after the caller read the person, and nothing was written, the stamp
    ///     included.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Targeted rather than a whole-row save for the reason <see cref="RotateSecurityStampAsync" />
    ///         is: a save of the copy read when the token was found would write back every other
    ///         column as it was then, and so undo an address change, or a restriction, that landed
    ///         meanwhile. The address in the condition is what orders a reset against an address
    ///         change (FR-PE-17): a token read before the change cannot set a password after it.
    ///     </para>
    ///     <para>
    ///         The stamp is replaced in the same statement, so no sign-in can read the new password
    ///         beside the old stamp or the old password beside the new one, and a refused write
    ///         leaves the stamp — which a restricted person's subject-rights token carries — as it
    ///         was. The password columns are written by nothing else once the row exists: the
    ///         mapping keeps every whole-row save off them, as it does the stamp.
    ///     </para>
    /// </remarks>
    Task<bool> TrySetPasswordAsync(Person person, byte[] passwordHash, byte[] salt, DateTime now);

    /// <summary>
    ///     UC-14 (FR-EV-08): marks the person's address verified and stamps <c>UpdatedAt</c>, writing
    ///     no other column, under the same condition as <see cref="TrySetPasswordAsync" />: the
    ///     address is the one the person was read with, and the record is not deleted, restricted or
    ///     anonymised. <c>false</c> means nothing was written.
    /// </summary>
    Task<bool> TryMarkEmailVerifiedAsync(Person person, DateTime now);

    /// <summary>
    ///     UC-56: records a token exchange — sets <c>LastUsedAt</c> to <paramref name="now" /> and writes
    ///     no other column — while the credential is still live at <paramref name="now" />
    ///     (<see cref="ApplicationCredential.LiveAt" />). <c>false</c> means it was revoked or expired
    ///     after the caller read it, and no token may be issued from it.
    /// </summary>
    Task<bool> TryStampCredentialUseAsync(ApplicationCredential credential, DateTime now);

    /// <summary>
    ///     UC-55: revokes a credential at <paramref name="revokedAt" />. Once: <c>false</c> means it was
    ///     already revoked, possibly after the caller read it, and the first revocation's time stands
    ///     (AF-55d).
    /// </summary>
    Task<bool> TryRevokeCredentialAsync(ApplicationCredential credential, DateTime revokedAt);

    /// <summary>
    ///     FR-AU-11: gives the person a fresh <see cref="Person.SecurityStamp" />, writing no other
    ///     column, so every token issued before this call is refused from the next request on.
    /// </summary>
    /// <remarks>
    ///     Unconditional, unlike the writes above: any number of rotations may race and every one of
    ///     them revokes the tokens issued before it. What has to be targeted is the write itself. A
    ///     whole-row save of a copy read before the rotation would put the old stamp back and revive
    ///     what it revoked, which is why the mapping never lets such a save write this column.
    /// </remarks>
    Task RotateSecurityStampAsync(Person person);

    /// <summary>
    ///     UC-08 and UC-23: writes the person's address and role — the email, <c>EmailVerified</c>
    ///     cleared when the address changes (AF-08b), the role with <c>ScopeId</c> cleared and the
    ///     <c>SCOPE_USER</c> row removed when the role changes (FR-PE-10), and <c>UpdatedAt</c> — while
    ///     the person still holds the address and role they were read with and is neither logically
    ///     deleted nor anonymised. Then, in the same transaction, runs <paramref name="saveAlongside" />:
    ///     the caller's own writes of what the change brings with it, the ownership row or the name.
    /// </summary>
    /// <param name="person">The person as the caller read them.</param>
    /// <param name="email">The address to hold; the one read for no address change.</param>
    /// <param name="roleId">The role to hold; the one read for no role change.</param>
    /// <param name="now">The time stamped as <c>UpdatedAt</c>.</param>
    /// <param name="saveAlongside">
    ///     Runs only once the change is written, and returns whether its writes succeeded. When they did
    ///     not, the change is rolled back with them.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         The address, its verification flag and the role are written only here, and the mapping
    ///         keeps every whole-row save off them. A save of a copy read before an address change
    ///         would otherwise put the old address back, or the verification flag that belonged to it;
    ///         one read before a role change, the old role.
    ///     </para>
    ///     <para>
    ///         The address and the flag go in one statement, so no verification read against the old
    ///         address can land between them (FR-PE-17): <see cref="TryMarkEmailVerifiedAsync" />
    ///         writes only while the address is the one it read. A refusal leaves nothing pending on
    ///         the caller's tracked entities, so a later save of anything else in the same unit of
    ///         work cannot write half of the change.
    ///     </para>
    ///     <para>
    ///         The <c>SCOPE_USER</c> row is deleted here, beside <c>ScopeId</c>, rather than by a save of the
    ///         person aggregate (#205): that save would write the person's plain profile back as the caller
    ///         read it, over a rename made meanwhile. No role a change can reach has a membership.
    ///     </para>
    /// </remarks>
    Task<AccountChangeOutcome> TryChangeEmailAndRoleAsync(
        Person person, string email, long roleId, DateTime now, Func<Task<bool>> saveAlongside);

    /// <summary>
    ///     UC-08: renames the person — <c>Name</c> and <c>UpdatedAt</c>, and no other column — while they
    ///     are not logically deleted (AF-08a) and still hold the role they were read with. <c>false</c>
    ///     means one of those changed, possibly since the caller read them, and nothing was written.
    /// </summary>
    /// <remarks>
    ///     Targeted rather than a whole-row save (#205), so a restriction, an erasure request or any other
    ///     change made after the caller read the person stands beside the new name. The role is in the
    ///     condition because it decided who may rename them (UC-08 step 3): a Scope Admin's rename of a
    ///     User must not land on a System Admin that User became meanwhile.
    /// </remarks>
    Task<bool> TryRenameAsync(Person person, string name, DateTime now);

    /// <summary>
    ///     UC-03: writes the scope's details — its name, description, default legal basis and privacy
    ///     notice URI, and <c>UpdatedAt</c>, and no other column — while it is not logically deleted
    ///     (AF-03a).
    /// </summary>
    /// <remarks>
    ///     Targeted rather than a whole-row save (#205): Google sign-in, self-registration and the email
    ///     links have endpoints of their own (UC-24, FR-SR-02, the email-links one), and a save of the
    ///     copy read here would write them back as read, over a change one of those made meanwhile — and
    ///     the deletion flag with them, reviving a scope deleted meanwhile.
    /// </remarks>
    Task<ScopeUpdateOutcome> TryUpdateScopeDetailsAsync(Scope scope, string name, string? description,
        int? defaultLegalBasis, string? privacyNoticeUri, DateTime now);

    /// <summary>
    ///     UC-04: logically deletes the scope — <c>IsDeleted</c> and <c>UpdatedAt</c>, and no other column —
    ///     unless it already is. <c>false</c> means it was, and nothing was written.
    /// </summary>
    /// <remarks>
    ///     Targeted for the reason <see cref="TryUpdateScopeDetailsAsync" /> is (#205): the copy the
    ///     deletion read can be older than a settings change made before it took its lock.
    /// </remarks>
    Task<bool> TryMarkScopeDeletedAsync(Scope scope, DateTime at);

    /// <summary>
    ///     UC-24: sets the scope's <c>GoogleSignInEnabled</c> and <c>UpdatedAt</c>, and no other column,
    ///     while it is not logically deleted (AF-24a). <c>false</c> means it was deleted, possibly since the
    ///     caller read it, and nothing was written.
    /// </summary>
    /// <remarks>
    ///     Targeted for the reason <see cref="TryUpdateScopeDetailsAsync" /> is (#205), and so are
    ///     <see cref="TrySetSelfRegistrationAsync" /> and <see cref="TrySetEmailLinksAsync" />: each setting
    ///     has an endpoint of its own, and none may write back what another, or UC-03, or UC-04, wrote.
    /// </remarks>
    Task<bool> TrySetGoogleSignInAsync(Scope scope, bool enabled, DateTime now);

    /// <summary>
    ///     FR-SR-02: sets the scope's <c>SelfRegistrationEnabled</c> and <c>UpdatedAt</c>, and no other
    ///     column, while it is not logically deleted. <c>false</c> means nothing was written.
    /// </summary>
    Task<bool> TrySetSelfRegistrationAsync(Scope scope, bool enabled, DateTime now);

    /// <summary>
    ///     Sets the scope's email links — <c>EmailVerificationUrl</c> and <c>PasswordResetUrl</c> — and
    ///     <c>UpdatedAt</c>, and no other column, while it is not logically deleted. <c>false</c> means
    ///     nothing was written.
    /// </summary>
    Task<bool> TrySetEmailLinksAsync(Scope scope, string? emailVerificationUrl, string? passwordResetUrl, DateTime now);

    /// <summary>
    ///     UC-28: logically deletes the Google User — <c>IsDeleted</c>, <c>DeletedAt</c>,
    ///     <c>DeletionKind</c> and <c>UpdatedAt</c>, and no other column — unless they already are.
    ///     <c>false</c> means they were, possibly since the caller read them, and the first deletion's time
    ///     and kind stand.
    /// </summary>
    /// <remarks>
    ///     A Google User's lifecycle columns — deletion, erasure request, restriction and anonymisation —
    ///     are written only by the targeted writes for Google Users here, as a person's are (#237, #205):
    ///     the mapping keeps every whole-row save off them, so a save of a copy read before one of those
    ///     changes cannot put the earlier state back.
    /// </remarks>
    Task<bool> TryMarkDeletedAsync(GoogleUser googleUser, DeletionKinds kind, DateTime at);

    /// <summary>
    ///     UC-44 for a Google User, as <see cref="TryRestrictProcessingAsync(Person, int, DateTime)" /> is
    ///     for a person: only while processing is not already restricted and the record is not anonymised.
    /// </summary>
    Task<bool> TryRestrictProcessingAsync(GoogleUser googleUser, int ground, DateTime at);

    /// <summary>
    ///     UC-45 for a Google User, as <see cref="TryLiftRestrictionAsync(Person, DateTime?, DateTime)" />
    ///     is for a person: only the restriction the caller read.
    /// </summary>
    Task<bool> TryLiftRestrictionAsync(GoogleUser googleUser, DateTime? notifiedAt, DateTime now);

    /// <summary>
    ///     UC-42 for a Google User: records the request and its deadline, lifts any restriction as UC-45
    ///     does for a subject lifting their own, and suspends them — nothing can block a Google User's
    ///     erasure (FR-GO-04) — only while no request was recorded and they are neither deleted nor
    ///     anonymised. <c>false</c> means one of those changed after the caller read them.
    /// </summary>
    Task<bool> TryRecordErasureRequestAsync(GoogleUser googleUser, DateTime requestedAt, DateTime dueAt);

    /// <summary>
    ///     FR-GO-19: sets the Google User's <c>EmailVerified</c> to what the token just verified says, and
    ///     <c>UpdatedAt</c>, only while they may sign in as the caller found them: not deleted, not
    ///     restricted (NFR-24) and not anonymised. <c>false</c> means one of those changed since.
    /// </summary>
    Task<bool> TryRefreshEmailVerifiedAsync(GoogleUser googleUser, bool emailVerified, DateTime now);

    /// <summary>
    ///     NFR-20 for a Google User: the placeholder name, address and Google subject, the picture and the
    ///     verification flag cleared, <c>AnonymisedAt</c> and <c>UpdatedAt</c> stamped — while they are
    ///     still deleted, not yet anonymised, and not restricted (NFR-24). <c>false</c> means one of those
    ///     changed after the caller selected them, and nothing was written.
    /// </summary>
    Task<bool> TryAnonymiseAsync(GoogleUser googleUser, string name, string email, string googleId, DateTime at);

    /// <summary>
    ///     NFR-25 for a Google User a restore brought back: anonymised as <see cref="TryAnonymiseAsync(GoogleUser,
    ///     string, string, string, DateTime)" /> does and marked a subject-requested deletion — dated when they
    ///     were deleted, or <paramref name="at" /> if the restored row is live — whatever their state, so long
    ///     as they are not anonymised already.
    /// </summary>
    Task<bool> TryReapplyErasureAsync(GoogleUser googleUser, string name, string email, string googleId, DateTime at);

    /// <summary>
    ///     UC-09 and UC-04's cascade: logically deletes the person — <c>IsDeleted</c>, <c>DeletedAt</c>,
    ///     <c>DeletionKind</c> and <c>UpdatedAt</c>, and no other column — unless they already are.
    ///     <c>false</c> means they were, possibly since the caller read them, and the first deletion's
    ///     time and kind stand.
    /// </summary>
    Task<bool> TryMarkDeletedAsync(Person person, DeletionKinds kind, DateTime at);

    /// <summary>
    ///     UC-42: records the person's erasure request — when it was made and its deadline — lifts any
    ///     restriction on processing as UC-45 does for a subject lifting their own, and either suspends
    ///     the person (a subject-requested logical deletion dated <paramref name="requestedAt" />) or,
    ///     when <paramref name="blockedReason" /> is given (AF-42d), records why it cannot yet. Only
    ///     while no request was recorded and the person is neither deleted nor anonymised: <c>false</c>
    ///     means one of those changed after the caller read them, and nothing was written.
    /// </summary>
    Task<bool> TryRecordErasureRequestAsync(
        Person person, DateTime requestedAt, DateTime dueAt, string? blockedReason);

    /// <summary>
    ///     NFR-20: suspends a person whose erasure request was blocked (AF-42d) once the block has
    ///     cleared — the logical deletion dated from the request, and the block's reason cleared — while
    ///     the request is still blocked, the person not deleted, and processing not restricted.
    ///     <c>false</c> means one of those changed after the caller read them.
    /// </summary>
    Task<bool> TrySuspendBlockedErasureAsync(Person person, DateTime now);

    /// <summary>
    ///     UC-44: restricts processing on <paramref name="ground" /> from <paramref name="at" />,
    ///     clearing any earlier lift notice, while processing is not already restricted and the record
    ///     is not anonymised. <c>false</c> means it was restricted, possibly since the caller read it,
    ///     and the first restriction stands.
    /// </summary>
    Task<bool> TryRestrictProcessingAsync(Person person, int ground, DateTime at);

    /// <summary>
    ///     UC-45: lifts the restriction the caller read — the one in force since the time the instance
    ///     holds — recording <paramref name="notifiedAt" /> as when the subject was told (Art. 18(3)), or
    ///     <c>null</c> when nobody had to be. <c>false</c> means it was lifted, or replaced by another,
    ///     after the caller read it, so a notice sent about one restriction cannot lift a later one.
    /// </summary>
    Task<bool> TryLiftRestrictionAsync(Person person, DateTime? notifiedAt, DateTime now);

    /// <summary>
    ///     NFR-20: anonymises a logically deleted person — the placeholder name and address, the
    ///     password hash and salt emptied, the verification flag, the login lockout, the factor
    ///     budgets, the account-email budget and any erasure block cleared, and <c>AnonymisedAt</c> and
    ///     <c>UpdatedAt</c> stamped — while they are still deleted, not yet anonymised, and not under a
    ///     restriction (NFR-24). <c>false</c> means one of those changed after the caller selected
    ///     them, and nothing was written.
    /// </summary>
    /// <remarks>
    ///     One statement, so a record is either anonymised whole or not at all: never left with its
    ///     name gone and its password hash kept, which the next pass would no longer select.
    /// </remarks>
    Task<bool> TryAnonymiseAsync(Person person, string name, string email, DateTime at);

    /// <summary>
    ///     NFR-25: anonymises a person a restore brought back, as <see cref="TryAnonymiseAsync" /> does,
    ///     and marks them a subject-requested deletion — dated when they were deleted, or
    ///     <paramref name="at" /> if the restored row is live — whatever their state, so long as they are
    ///     not anonymised already. <c>false</c> means they were, possibly since the caller read them.
    /// </summary>
    Task<bool> TryReapplyErasureAsync(Person person, string name, string email, DateTime at);

    /// <summary>
    ///     UC-21: makes the person an owner of scope <paramref name="scopeId" /> — inserts the
    ///     <c>SCOPE_OWNER</c> row and writes nothing on the person — while they are a live
    ///     <c>ScopeAdmin</c> or <c>SystemAdmin</c> (AF-21b) and do not own it already (AF-21d).
    ///     <c>false</c> means one of those changed after the caller read them, and nothing was written.
    /// </summary>
    /// <remarks>
    ///     A row of its own rather than a save of the person aggregate (#237): that save would write the
    ///     person's plain profile back as the caller read it, over any change made meanwhile. On success
    ///     the row joins the instance's <see cref="Person.ScopeOwnerships" />.
    /// </remarks>
    Task<bool> TryAddScopeOwnerAsync(Person person, long scopeId);

    /// <summary>
    ///     UC-22: removes the person's <c>SCOPE_OWNER</c> row for scope <paramref name="scopeId" /> —
    ///     writing nothing on the person — while another active person (<see cref="Person.Active" />) owns
    ///     the scope (AF-22b, NFR-12). <c>false</c> means the row was already gone, or no active co-owner is
    ///     left, and nothing was removed.
    /// </summary>
    /// <remarks>
    ///     The co-owner condition is evaluated by the same statement that deletes, so a co-owner deleted
    ///     between the caller's check and this write is seen. On success the row leaves the instance's
    ///     <see cref="Person.ScopeOwnerships" />.
    /// </remarks>
    Task<bool> TryRemoveScopeOwnerAsync(Person person, long scopeId);

    /// <summary>
    ///     FR-GO-21: records that a Google ID token was accepted, under <paramref name="tokenHash" /> —
    ///     the digest of its <c>jti</c>, or of its signed part when it carries none
    ///     (<see cref="GoogleIdTokenUse.KeyOf" />) — until <paramref name="expiresAt" />, the token's own
    ///     expiry. <c>false</c> means it was already recorded: the token was used before, and must be
    ///     refused as an invalid one is.
    /// </summary>
    /// <remarks>
    ///     One <c>INSERT … ON CONFLICT DO NOTHING</c>, so of any number of simultaneous requests
    ///     presenting one token exactly one records it. The row is kept until the token has expired
    ///     and the single-use grace has passed, then purged (NFR-19).
    /// </remarks>
    Task<bool> TryRecordGoogleIdTokenUseAsync(string tokenHash, DateTime expiresAt);
}
