using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     UC-47 steps 4 to 6, run in the background for a <see cref="RegistrationRequest" />: stores a
///     <see cref="PendingRegistration" /> and mails its confirmation link when the address is free in the
///     scope, or tells the address's holder of the attempt, at the address the holder registered with,
///     when it is not (AF-47b). No <c>Person</c> is created here: the verified <c>User</c> exists only
///     once the link is followed (UC-14).
/// </summary>
/// <remarks>
///     <para>
///         Off the request path for the reason <see cref="PasswordRecoveryProcessor" /> is: the lookup,
///         the insert and the email take a different time depending on the answer, and the answer is
///         what the uniform response exists to withhold.
///     </para>
///     <para>
///         The scope is re-read and re-checked because it may have been closed or deleted while the
///         request waited. The uniqueness rule is <see cref="Handlers.CreateUserCommandHandler" />'s:
///         live persons of the scope and every Google User of the scope, compared case-insensitively.
///         Pending registrations are not part of it: any number may wait for one address, and the first
///         link followed wins (the confirmer re-checks the rule when it creates the <c>User</c>).
///     </para>
///     <para>
///         Every failure here is logged with a fixed message and no address (NFR-22), and none is thrown:
///         the caller already has the uniform answer.
///     </para>
/// </remarks>
public class RegistrationProcessor(
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<GoogleUser, long> googleUserReader,
    IAsyncRepository<PendingRegistration, long> pendingWriter,
    IRegistrationLinkSender linkSender,
    IRegistrationAttemptSender attemptSender,
    EmailVerificationOptions options,
    ILogger<RegistrationProcessor> logger)
{
    public async Task ProcessAsync(RegistrationRequest request)
    {
        var scope = await scopeReader.Query().FirstOrDefaultAsync(x =>
            x.PublicId == request.ScopeId && !x.IsDeleted && x.SelfRegistrationEnabled);

        if (scope is null)
        {
            return;
        }

        var email = request.Email.ToLower();

        var holder = await personReader.Query().FirstOrDefaultAsync(person =>
            !person.IsDeleted && person.Email.ToLower() == email &&
            person.ScopeMembership != null && person.ScopeMembership.ScopeId == scope.Id);

        if (holder is not null)
        {
            // NFR-24: a restricted identity is not mailed, not even to say someone tried.
            if (holder.ProcessingRestrictedAt is null)
            {
                await attemptSender.SendAsync(holder.Email, googleAccount: false);
            }

            return;
        }

        var googleHolder = await googleUserReader.Query().FirstOrDefaultAsync(googleUser =>
            googleUser.ScopeId == scope.Id && googleUser.Email.ToLower() == email);

        if (googleHolder is not null)
        {
            // The address stays blocked either way (FR-GO-07); a deleted or restricted Google holder
            // is not told, since it could not act on it — or, if restricted, must not be processed.
            if (!googleHolder.IsDeleted && googleHolder.ProcessingRestrictedAt is null)
            {
                await attemptSender.SendAsync(googleHolder.Email, googleAccount: true);
            }

            return;
        }

        var token = SingleUseToken.New();
        var now = DateTime.UtcNow;

        var creation = await pendingWriter.CreateAsync(new PendingRegistration
        {
            ScopeId = scope.Id,
            Name = request.Name,
            Email = request.Email,
            PasswordHash = request.PasswordHash,
            Salt = request.Salt,
            TokenHash = SingleUseTokenHash.Of(token),
            ExpiresAt = now.Add(options.TokenLifetime),
            CreatedAt = now
        });

        if (!creation.Success)
        {
            // No address in the log (NFR-22). The caller has their 202; this line is the only trace.
            logger.LogError(
                "A pending registration could not be stored: {Errors}", string.Join(" | ", creation.Errors));

            return;
        }

        // The link follows UC-14's route, which recognises the token as a pending one; the mail asks for
        // the sign-up password too.
        await linkSender.SendAsync(request.Email, token);
    }
}
