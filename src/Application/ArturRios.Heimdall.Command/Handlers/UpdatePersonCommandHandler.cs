using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="UpdatePersonCommand" /> (UC-08): validates the request, loads the person
///     (AF-08a), enforces the per-actor rule, applies an optional role change (AF-08c, NFR-12), then
///     the name and email — re-checking uniqueness per FR-PE-09 and clearing <c>EmailVerified</c>
///     when the address changes (AF-08b). All failures are returned as errors on the output rather
///     than thrown.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why an own email change needs the password.</b> An address change requires proof of
///         identity, because recovery follows the address, and a bearer token is not proof that the
///         account holder is present. So a caller changing their own address presents their
///         current password, and their second factor when it is active (FR-PE-14), checked as UC-39
///         checks them, charged to the same re-authentication budget, and answered with UC-39's one
///         refusal (AF-08h, AF-08i). Both are checked before
///         anything that depends on the new address, so a caller without them cannot even learn
///         whether it is taken. An administrator changing someone else's address presents nothing of
///         theirs: the administrator's own authorisation covers it, as it always has.
///     </para>
///     <para>
///         <b>What follows any email change.</b> Whoever made it, the person's security stamp is
///         replaced, which revokes every token they hold (FR-AU-11), every live verification and
///         password-reset link is retired (FR-PE-17), and the previous address is told (FR-PE-15) —
///         so a change its owner did not make does not go unnoticed, and nothing mailed to the old
///         address still acts on the account.
///     </para>
/// </remarks>
public class UpdatePersonCommandHandler(
    IValidator<UpdatePersonCommand> validator,
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<GoogleUser, long> googleUserReader,
    IAsyncRepository<Person, long> personWriter,
    IScopeOwnershipChecker scopeOwnership,
    IAsyncRepository<EmailVerificationToken, long> verificationTokenWriter,
    IAsyncRepository<PasswordResetToken, long> resetTokenWriter,
    IAtomicWrites atomicWrites,
    IAsyncReadOnlyRepository<TwoFactorAuth, long> twoFactorReader,
    ITwoFactorFactorVerifier factorVerifier,
    IEmailChangeNoticeSender emailChangeNoticeSender,
    AdminEmailOptions emailOptions)
    : ICommandHandlerAsync<UpdatePersonCommand, UpdatePersonCommandOutput>
{
    /// <summary>The length of an authenticator-app or email code, which is all digits; a recovery code is not.</summary>
    private const int CodeLength = 6;

    public async Task<DataOutput<UpdatePersonCommandOutput?>> HandleAsync(UpdatePersonCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<UpdatePersonCommandOutput?>.New;

        // Step 2: validate input shape.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-08a: the person must exist and not be logically deleted. Both scope navigations are
        // included because severing them is how the join rows get deleted (see the design doc), and
        // each join row's Scope is included too because the response reports scope PublicIds.
        var person = await personReader.Query()
            .Include(x => x.ScopeMembership)
            .ThenInclude(membership => membership!.Scope)
            .Include(x => x.ScopeOwnerships)
            .ThenInclude(ownership => ownership.Scope)
            .FirstOrDefaultAsync(x => x.PublicId == command.Id && !x.IsDeleted);

        if (person is null)
        {
            return output.WithError(PersonMessages.PersonNotFound);
        }

        // UC-08 step 3: a System Admin may update anyone; anyone may update themselves; a Scope Admin
        // may update a User of a scope they own.
        if (!await MayUpdateAsync(command, person))
        {
            return output.WithError(PersonMessages.NotAuthorizedToUpdatePerson);
        }

        // UC-08 step 5, decided but not yet applied: every refusal below has to leave the loaded
        // person untouched, so the role change is validated first and written further down, once
        // nothing else can reject the request.
        var roleChange = await ValidateRoleChangeAsync(command, person);

        if (roleChange.Error is not null)
        {
            return output.WithError(roleChange.Error);
        }

        // UC-08 step 4: an email change re-checks uniqueness and clears the verification flag.
        //
        // Detected ordinally, on the trimmed values as stored, and nothing else decides it: this one
        // flag gates re-authentication, the verification flag, the stamp rotation, the notice and the
        // token retirement below. A case-insensitive comparison folds distinct characters together
        // (final and medial sigma, for one), and two such addresses can name different mailboxes.
        // Read ordinally, a change of case is a change too, which asks for the password and sends a
        // notice: it fails closed.
        var emailChanged = !string.Equals(person.Email.Trim(), command.Email.Trim(), StringComparison.Ordinal);

        // What is written when it did change: trimmed, because sign-in and recovery match the stored
        // address exactly, case aside, and a padded copy would no longer be found. When it did not,
        // the stored value is left exactly as it is.
        var newEmail = emailChanged ? command.Email.Trim() : person.Email;

        // UC-08 step 4 (FR-PE-14, AF-08h, AF-08i): changing your own address needs your password, and
        // your second factor when it is active. Before the uniqueness check, so without them a caller
        // learns nothing about the new address either.
        var reauthentication = emailChanged && command.ActingPersonId == person.PublicId
            ? await ReauthenticateAsync(command, person)
            : Reauthentication.NotRequired;

        if (reauthentication.Error is not null)
        {
            return output.WithError(reauthentication.Error);
        }

        // A role change re-checks it too, even on an unchanged address (FR-PE-09). The namespace the
        // address has to be unique in is chosen by the role, so promoting a User to System Admin
        // moves their address out of their scope's space and into the system-wide admin one, where a
        // different set of people may already hold it. Checking only on an email change let that
        // promotion create two live admins sharing an address — after which UC-11's admin lookup
        // (FirstOrDefaultAsync) resolves to one of them and the other can never log in again.
        if ((emailChanged || roleChange.Applies) &&
            await EmailTakenAsync(command, person, roleChange.TargetRoleId))
        {
            return output.WithError(PersonMessages.EmailAlreadyExists);
        }

        // FR-PE-16 (AF-08j): an own change mails the previous address (FR-PE-15), so it is charged to
        // the person's account-email budget — after the password and factor were accepted, so a token
        // without them cannot spend it, and before the factor is spent or anything is written, so a
        // refusal costs no code and changes nothing. Switching an address back and forth therefore
        // mails the address it leaves at most the budget's day. An administrator's change is not
        // charged: it is their authority, not the person's own request.
        if (emailChanged && command.ActingPersonId == person.PublicId &&
            AccountEmailBudget.RefusalOf(
                await AccountEmailBudget.TryReserveAsync(atomicWrites, person, emailOptions)) is { } mailRefusal)
        {
            return output.WithError(mailRefusal);
        }

        // The second factor is spent only now, once nothing else can refuse the request — and by a
        // write only one request can win, as everywhere a code is accepted.
        var spending = await SpendFactorAsync(reauthentication.Verification);

        if (spending is not null)
        {
            return output.WithError(spending);
        }

        // FR-PE-17: the links that act on the account through its address — password reset and
        // verification — were mailed to the address being replaced, and go with it. Retired here,
        // before the new address is written, so none can be followed once it is; and again after
        // the save, for any mailed to the old address in between. Here also means before the person
        // is modified below, since each retirement saves. A reset or verification already under
        // way cannot land after the change either: both write only while the address is the one
        // they read (IAtomicWrites.TrySetPasswordAsync, TryMarkEmailVerifiedAsync).
        if (emailChanged && await RetireAddressLinksAsync(person.Id) is { } earlyRetirement)
        {
            return output.WithErrors(earlyRetirement);
        }

        var previousEmail = person.Email;

        if (emailChanged)
        {
            person.EmailVerified = false;
        }

        // FR-PE-10: a System Admin belongs to no scope, so the membership and ownership rows are
        // severed — which deletes them, since both relationships are required and cascade.
        if (roleChange.Applies)
        {
            person.RoleId = roleChange.TargetRoleId;
            person.ScopeMembership = null;
            person.ScopeId = null;
            person.ScopeOwnerships.Clear();
        }

        // FR-AU-11: the address is what account recovery is sent to, so a new one is a credential
        // change and revokes every token the person holds — whoever makes the change, and when they
        // make it themselves, the token making it too. Before the save, in its own targeted write: if
        // the save then fails, the cost is one extra sign-in, where the other order could leave a
        // changed address with its old sessions live.
        if (emailChanged)
        {
            await atomicWrites.RotateSecurityStampAsync(person);
        }

        // UC-08 step 6: apply and stamp UpdatedAt (no DB trigger maintains it).
        person.Name = command.Name;
        person.Email = newEmail;
        person.UpdatedAt = DateTime.UtcNow;

        var update = await personWriter.UpdateAsync(person);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        // FR-AU-11 again, once the new address is saved: a sign-in between the first rotation and
        // the save read the first new stamp beside the old address, and its token must not outlive
        // the change. The first rotation is what makes a failed save safe; this one closes that gap.
        if (emailChanged)
        {
            await atomicWrites.RotateSecurityStampAsync(person);
        }

        if (emailChanged)
        {
            // FR-PE-15: the previous address is told. Only that address is handed over, so the
            // notice cannot disclose the new one; a failed delivery is the sender's to log, and does
            // not fail an update that has already been made.
            await emailChangeNoticeSender.SendAsync(previousEmail);

            // UC-08 step 4, continued (FR-PE-17): a verification token is proof of control of the
            // address it was mailed to, and that is no longer the person's address. Left live, one
            // mailed to the old address would mark the new one verified through UC-14 without
            // anybody having read anything sent to it — undoing the reset just made. A reset token
            // mailed there would let that mailbox set the password. The second pass of the two.
            if (await RetireAddressLinksAsync(person.Id) is { } retirement)
            {
                return output.WithErrors(retirement);
            }
        }

        // UC-08 step 7: return the updated person.
        return output
            .WithData(new UpdatePersonCommandOutput
            {
                Id = person.PublicId,
                Name = person.Name,
                Email = person.Email,
                Role = (int)person.RoleId,
                EmailVerified = person.EmailVerified,
                ScopeId = person.ScopeMembership?.Scope.PublicId,
                OwnedScopeIds = person.ScopeOwnerships.Select(ownership => ownership.Scope.PublicId).ToList(),
                CreatedAt = person.CreatedAt,
                UpdatedAt = person.UpdatedAt
            })
            .WithMessage(PersonMessages.PersonUpdatedSuccessfully);
    }

    /// <summary>
    ///     FR-PE-17: retires every live verification and password-reset link the person holds.
    ///     Returns the persistence errors of the first failed write, or <c>null</c>.
    /// </summary>
    private async Task<IEnumerable<string>?> RetireAddressLinksAsync(long personId)
    {
        var now = DateTime.UtcNow;

        return await EmailVerificationTokenRetirement.RetireLiveAsync(
                   verificationTokenWriter, verificationTokenWriter, personId, now)
               ?? await PasswordResetTokenRetirement.RetireLiveAsync(
                   resetTokenWriter, resetTokenWriter, personId, now);
    }

    /// <summary>
    ///     The outcome of re-authenticating a caller who changes their own email (FR-PE-14).
    /// </summary>
    /// <param name="Error">The canonical refusal, or <c>null</c> when the caller proved who they are.</param>
    /// <param name="Verification">
    ///     The second factor that matched, still to be spent, or <c>null</c> when none was required.
    /// </param>
    private sealed record Reauthentication(string? Error, TwoFactorFactorVerificationResult? Verification)
    {
        public static readonly Reauthentication NotRequired = new(null, null);

        public static Reauthentication Refused(string error) => new(error, null);
    }

    /// <summary>
    ///     FR-PE-14: the current password, through <see cref="PasswordHashGate" /> as every password
    ///     check is (Threat Model TH-03), then — when the person's two-factor authentication is
    ///     active — a current second factor, through the same <see cref="ITwoFactorFactorVerifier" />
    ///     UC-38, UC-39 and UC-40 use. Charged, once, to the account's re-authentication budget
    ///     (<see cref="ReauthenticationBudget" />, FR-AU-13), and refused as UC-39 refuses: one
    ///     message for a wrong password, a wrong factor and a paused budget alike.
    /// </summary>
    /// <remarks>
    ///     One field carries the second factor, so its shape decides how it is checked: six digits are
    ///     an authenticator-app or email code, anything else a recovery code, which is never six
    ///     characters long (<c>XXXX-XXXX-XXXX-XXXX</c>, or <c>XXXX-XXXX</c> for one issued before
    ///     those). A missing one is checked as a code and so refused like a wrong one.
    /// </remarks>
    private async Task<Reauthentication> ReauthenticateAsync(UpdatePersonCommand command, Person person)
    {
        // FR-AU-13: charged before anything is compared. A paused budget still has the credentials
        // compared, and refuses whatever the result.
        var admitted = await ReauthenticationBudget.TryChargeAsync(atomicWrites, person);

        // AF-08h: missing or wrong alike. Decided below, once the factor has been checked too, so a
        // wrong password takes as long to refuse as a wrong factor does.
        var passwordMatches = !string.IsNullOrWhiteSpace(command.CurrentPassword) &&
                              await PasswordHashGate.Shared.TextMatchesAsync(
                                  command.CurrentPassword, person.PasswordHash, person.Salt);

        var twoFactorAuth = await twoFactorReader.Query()
            .FirstOrDefaultAsync(x => x.PersonId == person.Id && x.IsActive);

        TwoFactorFactorVerificationResult? verification = null;

        if (twoFactorAuth is not null)
        {
            var factor = command.TwoFactorCode?.Trim();
            var isCode = factor is null || (factor.Length == CodeLength && factor.All(char.IsAsciiDigit));

            verification = await factorVerifier.VerifyAsync(
                twoFactorAuth, isCode ? factor : null, isCode ? null : factor);
        }

        // AF-08h and AF-08i, answered alike, and the pause with them.
        if (!passwordMatches || verification is { Matched: false } || !admitted)
        {
            return Reauthentication.Refused(AuthMessages.PasswordOrCodeIncorrect);
        }

        await ReauthenticationBudget.ClearAsync(atomicWrites, person);

        return new Reauthentication(null, verification);
    }

    /// <summary>
    ///     Spends the email code or recovery code that re-authenticated the caller, exactly as UC-40
    ///     spends the one that authorised it. An app code needs nothing more: the verifier already
    ///     recorded its time step (FR-2F-14). Returns the refusal when another request spent the code
    ///     first, or <c>null</c>.
    /// </summary>
    private async Task<string?> SpendFactorAsync(TwoFactorFactorVerificationResult? verification)
    {
        if (verification?.ConsumedEmailCode is { } emailCode &&
            !await atomicWrites.TryConsumeEmailCodeAsync(emailCode))
        {
            return AuthMessages.PasswordOrCodeIncorrect;
        }

        if (verification?.ConsumedRecoveryCode is { } recoveryCode &&
            !await atomicWrites.TryConsumeRecoveryCodeAsync(recoveryCode, DateTime.UtcNow))
        {
            return AuthMessages.PasswordOrCodeIncorrect;
        }

        return null;
    }

    /// <summary>
    ///     UC-08 step 3. A System Admin may update any person; any actor may update their own record;
    ///     a Scope Admin may update a <c>User</c> belonging to a scope they own. Everything else is
    ///     denied.
    /// </summary>
    private async Task<bool> MayUpdateAsync(UpdatePersonCommand command, Person person)
    {
        if (command.ActingRole == (int)Roles.SystemAdmin || command.ActingPersonId == person.PublicId)
        {
            return true;
        }

        if (command.ActingRole != (int)Roles.ScopeAdmin ||
            person.RoleId != (long)Roles.User ||
            person.ScopeMembership is null)
        {
            return false;
        }

        return await scopeOwnership.ActorMayManageScopeAsync(
            command.ActingRole, command.ActingPersonId, person.ScopeMembership.ScopeId);
    }

    /// <summary>
    ///     The outcome of examining UC-08's optional role change, before anything is written.
    /// </summary>
    /// <param name="Applies">Whether a role change was requested and is allowed to go ahead.</param>
    /// <param name="TargetRoleId">
    ///     The role the person will hold after this update — their current one when
    ///     <paramref name="Applies" /> is <see langword="false" />. The email-uniqueness rule is
    ///     chosen by this value, not by the role they hold now.
    /// </param>
    /// <param name="Error">The canonical message describing a refusal, or <c>null</c>.</param>
    private sealed record RoleChange(bool Applies, long TargetRoleId, string? Error)
    {
        public static RoleChange None(long currentRoleId) => new(false, currentRoleId, null);

        public static RoleChange Refused(long currentRoleId, string error) =>
            new(false, currentRoleId, error);
    }

    /// <summary>
    ///     UC-08 step 5, as a decision rather than a mutation. Only a change to <c>SystemAdmin</c> is
    ///     supported: every other target role would need a scope the request does not carry
    ///     (FR-PE-02, FR-PE-11).
    /// </summary>
    /// <remarks>
    ///     Nothing is written here, deliberately. The uniqueness check that follows it can still
    ///     refuse the request, and a handler that had already rewritten the loaded person's role
    ///     would be relying on the caller never saving — true today, since a refusal returns before
    ///     <c>UpdateAsync</c>, but true only by accident and invisible from here.
    /// </remarks>
    private async Task<RoleChange> ValidateRoleChangeAsync(UpdatePersonCommand command, Person person)
    {
        if (command.RoleId is null || command.RoleId == (int)person.RoleId)
        {
            return RoleChange.None(person.RoleId);
        }

        // AF-08c: only a System Admin may change a role.
        if (command.ActingRole != (int)Roles.SystemAdmin)
        {
            return RoleChange.Refused(person.RoleId, PersonMessages.RoleChangeRequiresSystemAdmin);
        }

        if (command.RoleId != (int)Roles.SystemAdmin)
        {
            return RoleChange.Refused(person.RoleId, PersonMessages.UnsupportedRoleTransition);
        }

        // NFR-12: a scope must always retain at least one owner. Gather the scopes somebody *other*
        // than this person owns; refuse if any scope this person owns is not among them. Persons
        // already logically deleted are excluded, since they can no longer authenticate and so do
        // not keep a scope owned — the same guard UC-09 and UC-10 apply.
        if (person.RoleId == (long)Roles.ScopeAdmin && person.ScopeOwnerships.Count > 0)
        {
            var ownedScopeIds = person.ScopeOwnerships.Select(ownership => ownership.ScopeId).ToList();

            var coOwnedScopeIds = await personReader.Query()
                .Where(other => other.Id != person.Id && !other.IsDeleted)
                .SelectMany(other => other.ScopeOwnerships.Select(ownership => ownership.ScopeId))
                .Distinct()
                .ToListAsync();

            if (ownedScopeIds.Any(scopeId => !coOwnedScopeIds.Contains(scopeId)))
            {
                return RoleChange.Refused(person.RoleId, PersonMessages.ScopeWouldLoseLastOwner);
            }
        }

        return new RoleChange(true, (long)Roles.SystemAdmin, null);
    }

    /// <summary>
    ///     FR-PE-09, evaluated against the role the person will have after this update: a
    ///     <c>User</c>'s email is unique within their scope — jointly with that scope's Google Users,
    ///     per FR-GO-07 — and an admin's is unique system-wide. The person being updated is excluded
    ///     so resubmitting their own address is not a conflict. Compared case-insensitively
    ///     (<c>LOWER()</c> in SQL), as UC-06 does.
    /// </summary>
    private async Task<bool> EmailTakenAsync(UpdatePersonCommand command, Person person, long targetRoleId)
    {
        var email = command.Email.Trim().ToLower();

        if (targetRoleId == (long)Roles.User && person.ScopeMembership is not null)
        {
            var scopeId = person.ScopeMembership.ScopeId;

            var takenByPerson = await personReader.Query().AnyAsync(other =>
                other.Id != person.Id && !other.IsDeleted && other.Email.ToLower() == email &&
                other.ScopeMembership != null && other.ScopeMembership.ScopeId == scopeId);

            if (takenByPerson)
            {
                return true;
            }

            // The scope's address space is shared with its Google Users (FR-GO-07), so moving a
            // User onto an address a Google User already holds is the same conflict as moving them
            // onto another User's — the same second read CreateUserCommandHandler makes.
            return await googleUserReader.Query().AnyAsync(googleUser =>
                googleUser.ScopeId == scopeId && googleUser.Email.ToLower() == email);
        }

        // An admin belongs to no scope, so no Google User can share their namespace: FR-GO-04 makes
        // every Google account User-equivalent within one scope.
        return await personReader.Query().AnyAsync(other =>
            other.Id != person.Id && !other.IsDeleted && other.Email.ToLower() == email &&
            (other.RoleId == (long)Roles.SystemAdmin || other.RoleId == (long)Roles.ScopeAdmin));
    }
}
