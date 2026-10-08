using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Random;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="LoginCommand" /> (UC-11): locates the person by the lookup their role
///     implies (FR-AU-01/02), verifies the password against the stored hash and salt, confirms
///     neither the person nor the scope backing them is logically deleted (FR-AU-05/06/07), and
///     issues a token carrying their <c>PublicId</c>, role, and scope claims (FR-AU-03/04) — unless
///     the person has active two-factor authentication, in which case AF-11g diverts to a short-lived
///     challenge token instead (FR-2F-07…FR-2F-08; see UC-38 for how the login is then completed).
/// </summary>
/// <remarks>
///     <para>
///         AF-11a…AF-11e all return the same <see cref="AuthMessages.InvalidCredentials" /> error, so
///         the endpoint reveals nothing about which emails exist or which accounts and scopes are
///         deleted. The checks nonetheless run in the specification's order, so the code reads
///         against UC-11.
///     </para>
///     <para>
///         AF-11a additionally verifies the submitted password against a decoy hash before answering.
///         Argon2id is deliberately expensive — 600 MB and 16 threads by the hashing library's
///         default — so a request that skipped it because no person matched returned in single-digit
///         milliseconds while every other rejection took hundreds. That gap is readable from outside
///         and answers the exact question the shared message exists to refuse: whether an address is
///         registered, and (by varying <see cref="LoginCommand.ScopeId" />) which scope it sits in.
///         One uniform message is not anti-enumeration on its own if the timing disagrees with it.
///     </para>
/// </remarks>
public class LoginCommandHandler(
    IValidator<LoginCommand> validator,
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<TwoFactorAuth, long> twoFactorReader,
    IAtomicWrites atomicWrites,
    ITwoFactorEmailCodeIssuer emailCodeIssuer,
    ITwoFactorChallengeTokenIssuer challengeTokenIssuer,
    PersonAuthTokenService personAuthTokenService)
    : ICommandHandlerAsync<LoginCommand, LoginCommandOutput>
{
    private const int MaxFailedLoginAttempts = 10;

    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    // A hash of a random secret nobody knows, used only to spend the same Argon2id work on AF-11a
    // that a real password check spends. Generated per process rather than hard-coded so it is never
    // a value an attacker could recognise, and computed once so the cost sits at start-up rather
    // than on the request that needs to look like every other request.
    private static readonly (byte[] Hash, byte[] Salt) Decoy = CreateDecoy();

    private static (byte[] Hash, byte[] Salt) CreateDecoy()
    {
        var hash = Hash.EncodeWithRandomSalt(
            CustomRandom.Text(new RandomStringOptions { Length = 32 }), out var salt);

        return (hash, salt);
    }

    public async Task<DataOutput<LoginCommandOutput?>> HandleAsync(LoginCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<LoginCommandOutput?>.New;

        // AF-11f: validate input shape.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // UC-11 step 2. The lookup deliberately omits an !IsDeleted filter: AF-11c exists to reject a
        // logically deleted person, so they must be found first.
        var person = await FindPersonAsync(command);

        // AF-11a. The decoy verification is what keeps this path indistinguishable from the ones
        // below by response time — see the remarks. Its result is discarded: the answer is already
        // decided, and only the work matters.
        if (person is null)
        {
            await VerifyAgainstDecoy(command.Password);

            return output.WithError(AuthMessages.InvalidCredentials);
        }

        var now = DateTime.UtcNow;

        // FR-AU-09: the attempt is charged before the password is considered, and an account that
        // is locked out — or whose remaining attempts parallel requests have already taken — is
        // refused without considering it at all. The decoy keeps the cost of that refusal equal to a
        // real check's, so a lockout cannot be detected by how quickly it answers.
        if (!await atomicWrites.TryReserveLoginAttemptAsync(person, MaxFailedLoginAttempts, now))
        {
            await VerifyAgainstDecoy(command.Password);

            return output.WithError(AuthMessages.InvalidCredentials);
        }

        // UC-11 step 3 (AF-11b). The reserved attempt stands as a failure; at the tenth the account
        // locks.
        if (!await PasswordHashGate.Shared.TextMatchesAsync(command.Password, person.PasswordHash, person.Salt))
        {
            await atomicWrites.LockOutIfExhaustedAsync(person, MaxFailedLoginAttempts, now.Add(LockoutDuration));

            return output.WithError(AuthMessages.InvalidCredentials);
        }

        // The password checked out: the threshold counts consecutive failures, so the count — this
        // attempt's reservation included — starts again.
        await atomicWrites.ClearLoginAttemptsAsync(person);

        // NFR-24 (UC-44 step 4): processing is restricted, so authenticating would be processing it.
        // Answered with the same message as every other refusal here, so the endpoint cannot be
        // used to discover that an account is under dispute — and the subject who asked for the
        // restriction already knows why.
        if (person.ProcessingRestrictedAt is not null)
        {
            return output.WithError(AuthMessages.InvalidCredentials);
        }

        // UC-11 step 4 (AF-11c, FR-AU-05).
        if (person.IsDeleted)
        {
            return output.WithError(AuthMessages.InvalidCredentials);
        }

        // UC-11 step 5 (AF-11d/AF-11e, FR-AU-06/07).
        if (!personAuthTokenService.TryBuildSubject(person, out var subject))
        {
            return output.WithError(AuthMessages.InvalidCredentials);
        }

        // UC-11 step 6, AF-11g (FR-2F-07): an active 2FA configuration diverts to a challenge token.
        var twoFactorAuth = await twoFactorReader.Query()
            .FirstOrDefaultAsync(x => x.PersonId == person.Id && x.IsActive);

        if (twoFactorAuth is not null)
        {
            return await IssueChallengeAsync(output, person, twoFactorAuth);
        }

        var token = await personAuthTokenService.IssueAsync(subject!);

        return output
            .WithData(new LoginCommandOutput
            {
                Token = token.Token, ExpiresAt = token.ExpiresAt, EmailVerified = person.EmailVerified
            })
            .WithMessage(AuthMessages.LoginSuccessful);
    }

    // FR-AU-09, how the count is kept.
    //
    // The per-IP limiter in Startup bounds how fast one source can guess; the count bounds how many
    // guesses an account will accept in total, which is the half a distributed attacker defeats by
    // spreading requests across addresses. The lockout is a window rather than a latch an
    // administrator has to clear: a permanent lock would hand any anonymous caller a denial of
    // service against any account whose address they know. Fifteen minutes cuts a sustained guessing
    // rate to a few hundred attempts a day while costing a caller who mistyped one coffee break.
    //
    // Each attempt reserves itself in the count before the password is derived (IAtomicWrites), and
    // a wrong password leaves the reservation standing. Counting after the derivation instead let a
    // burst of parallel guesses all read the same count, all pass the "not locked" check, and then
    // overwrite each other's increments — ten simultaneous wrong passwords were recorded as one, and
    // the lock never came. Reserving first means no more than ten derivations can ever run between
    // lockouts, however the requests are timed.

    /// <summary>
    ///     Runs one password verification against a hash that belongs to nobody, so that AF-11a costs
    ///     what AF-11b costs. The salt and hash are computed once, at type initialisation, from a
    ///     random secret no one holds; only the per-request Argon2id derivation is repeated, which is
    ///     the whole of the expense being matched.
    /// </summary>
    private static Task VerifyAgainstDecoy(string password) =>
        PasswordHashGate.Shared.TextMatchesAsync(password, Decoy.Hash, Decoy.Salt);

    /// <summary>
    ///     AF-11g: issues the short-lived challenge token instead of a full one, and — per FR-2F-08 —
    ///     a fresh email code when the Email method is enabled, through the same
    ///     <see cref="ITwoFactorEmailCodeIssuer" /> UC-36 and UC-46 use, so the retire-then-issue step
    ///     cannot drift between them.
    /// </summary>
    private async Task<DataOutput<LoginCommandOutput?>> IssueChallengeAsync(
        DataOutput<LoginCommandOutput?> output, Person person, TwoFactorAuth twoFactorAuth)
    {
        // FR-2F-10: this challenge becomes the configuration's one outstanding challenge, replacing
        // any earlier one, and UC-38 honours a challenge token only while it still names it — so a
        // challenge is redeemable once, and never after a newer login.
        //
        // UC-46's reissue budget is per authentication attempt (FR-2F-13), and so is the guess
        // budget of FR-2F-17: this challenge starts with all of both available, whatever the previous
        // one spent. All are written before the code goes out, so a send that fails cannot leave the
        // budget overstating what was used.
        //
        // Written as targeted columns rather than the whole row, so a login cannot write back a stale
        // copy of what a parallel request has just recorded — an accepted TOTP step, for one.
        var challengeId = Guid.NewGuid();

        await atomicWrites.StartChallengeAsync(twoFactorAuth, challengeId);

        if (twoFactorAuth.EmailEnabled)
        {
            var emailCodeErrors = await emailCodeIssuer.ReissueAsync(twoFactorAuth, person.Email);

            if (emailCodeErrors is not null)
            {
                return output.WithErrors(emailCodeErrors);
            }
        }

        var challenge = await challengeTokenIssuer.IssueAsync(person.PublicId, (int)person.RoleId, challengeId);

        var methods = new List<string>();

        if (twoFactorAuth.AppEnabled)
        {
            methods.Add("App");
        }

        if (twoFactorAuth.EmailEnabled)
        {
            methods.Add("Email");
        }

        return output
            .WithData(new LoginCommandOutput
            {
                RequiresTwoFactor = true, ChallengeToken = challenge.Token, AvailableMethods = methods
            })
            .WithMessage(AuthMessages.TwoFactorRequired);
    }

    /// <summary>
    ///     UC-11 step 2: a <c>User</c> is sought within the scope the request names, since their
    ///     email is only unique there (FR-AU-01); a <c>ScopeAdmin</c>/<c>SystemAdmin</c> is sought
    ///     system-wide among admins (FR-AU-02). Emails are compared case-insensitively (LOWER() in
    ///     SQL), matching how uniqueness is enforced when a person is created.
    /// </summary>
    private async Task<Person?> FindPersonAsync(LoginCommand command)
    {
        var email = command.Email.ToLower();

        var query = personReader.Query()
            .Include(person => person.ScopeMembership)
            .ThenInclude(membership => membership!.Scope)
            .Include(person => person.ScopeOwnerships)
            .ThenInclude(ownership => ownership.Scope)
            // Live persons first. Email uniqueness (FR-PE-09) holds among live persons only, so a
            // logically deleted person awaiting anonymisation can share an address with the live
            // person who replaced them. Without an order the database chose between the two, and
            // choosing the deleted one refused the live person for as long as the other remained.
            // A deleted person is still found when nobody live holds the address, which is what
            // lets the deleted-person check reject them.
            .OrderBy(person => person.IsDeleted);

        if (command.ScopeId is null)
        {
            return await query.FirstOrDefaultAsync(person =>
                person.Email.ToLower() == email &&
                (person.RoleId == (long)Roles.SystemAdmin || person.RoleId == (long)Roles.ScopeAdmin));
        }

        return await query.FirstOrDefaultAsync(person =>
            person.Email.ToLower() == email &&
            person.RoleId == (long)Roles.User &&
            person.ScopeMembership != null &&
            person.ScopeMembership.Scope.PublicId == command.ScopeId);
    }
}
