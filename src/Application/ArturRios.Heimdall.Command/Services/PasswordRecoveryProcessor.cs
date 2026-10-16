using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     UC-12 steps 2 to 4, run in the background for a <see cref="PasswordRecoveryRequest" />: finds
///     the person by the lookup their role implies, and issues and mails them a reset token if they
///     could use one (FR-PR-01/02). AF-12a — nobody holds the address — is the absence of that work.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this is not on the request path.</b> The answer UC-12 gives is the same on every
///         path, but the time it took was not: a registered address waited for a token insert and a
///         Mailgun round trip, an unknown one returned at once. That difference is readable from
///         outside and answers the question the uniform answer exists to refuse. The handler now only
///         queues the request, so the request path does the same work whoever the address belongs
///         to, and the lookup, the insert and the send all happen here, after the caller has their
///         answer.
///     </para>
///     <para>
///         Run in its own DI scope per request (<c>PasswordRecoveryDispatcher</c>), so the database
///         context and the Mailgun client it uses belong to this work and not to an HTTP request that
///         has already completed.
///     </para>
/// </remarks>
public class PasswordRecoveryProcessor(
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<PasswordResetToken, long> tokenReader,
    IPasswordResetService passwordReset,
    PasswordResetOptions resetOptions,
    AdminEmailOptions emailOptions,
    ILogger<PasswordRecoveryProcessor> logger)
{
    public async Task ProcessAsync(PasswordRecoveryRequest request)
    {
        // UC-12 step 2. As in UC-11, the lookup omits an !IsDeleted filter so a deleted person is
        // found and then declined below, rather than being indistinguishable from a missing row for
        // the wrong reason.
        var person = await FindPersonAsync(request);

        // UC-12 steps 3 and 4. Skipped entirely for AF-12a and for accounts that could not log in
        // anyway — sending a reset link to a deleted person's address would restore nothing.
        if (person is null || PasswordRecoveryEligibility.Of(person) != RecoveryEligibility.Eligible)
        {
            return;
        }

        // FR-PR-07 (AF-12b): one reset link per person per cooldown, whoever asks — asked exactly as
        // UC-52 asks it, so a link either sent counts. The caller was answered long ago, so a request
        // inside it simply sends nothing, and the link already sent keeps working: the cooldown is
        // held to the link's lifetime, so it never outlasts the last link sent. Requests are worked one
        // at a time per instance, so only a second instance could overlap one. No daily cap: recovery
        // is anonymous, and a cap anyone could spend would deny the owner their own recovery.
        var cooldown = emailOptions.CappedAt(resetOptions.TokenLifetime);

        if (await cooldown.IsRunningAsync(tokenReader, person.Id, resetOptions.TokenLifetime, DateTime.UtcNow))
        {
            // No address in the log (NFR-22).
            logger.LogInformation("A password reset was not sent: one went to the person within the cooldown");

            return;
        }

        await passwordReset.IssueAndSendAsync(person);
    }

    /// <summary>
    ///     UC-12 step 2, the same role-driven lookup as UC-11: a <c>User</c> is sought within the
    ///     scope the request names, since their email is only unique there; a <c>ScopeAdmin</c> or
    ///     <c>SystemAdmin</c> system-wide among admins. Emails are compared case-insensitively
    ///     (LOWER() in SQL), matching how uniqueness is enforced when a person is created.
    /// </summary>
    private async Task<Person?> FindPersonAsync(PasswordRecoveryRequest request)
    {
        var email = request.Email.ToLower();

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

        if (request.ScopeId is null)
        {
            return await query.FirstOrDefaultAsync(person =>
                person.Email.ToLower() == email &&
                (person.RoleId == (long)Roles.SystemAdmin || person.RoleId == (long)Roles.ScopeAdmin));
        }

        return await query.FirstOrDefaultAsync(person =>
            person.Email.ToLower() == email &&
            person.RoleId == (long)Roles.User &&
            person.ScopeMembership != null &&
            person.ScopeMembership.Scope.PublicId == request.ScopeId);
    }
}
