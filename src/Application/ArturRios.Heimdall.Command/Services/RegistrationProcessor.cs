using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     UC-47 steps 4 to 6, run in the background for a <see cref="RegistrationRequest" />: creates the
///     <c>User</c> and mails a verification link when the address is free in the scope, or tells the
///     address's holder of the attempt when it is not (AF-47b).
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
///     </para>
///     <para>
///         The dispatcher takes one request at a time, so two registrations of one address in one
///         process never race; the unique-violation branch covers two Heimdall instances doing so.
///     </para>
/// </remarks>
public class RegistrationProcessor(
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<GoogleUser, long> googleUserReader,
    IAsyncRepository<Person, long> personWriter,
    IEmailVerificationService emailVerification,
    IRegistrationAttemptSender attemptSender)
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
                await attemptSender.SendAsync(request.Email, googleAccount: false);
            }

            return;
        }

        var googleHolder = await googleUserReader.Query().FirstOrDefaultAsync(googleUser =>
            googleUser.ScopeId == scope.Id && googleUser.Email.ToLower() == email);

        if (googleHolder is not null)
        {
            if (googleHolder.ProcessingRestrictedAt is null)
            {
                await attemptSender.SendAsync(request.Email, googleAccount: true);
            }

            return;
        }

        var person = new Person
        {
            Name = request.Name,
            Email = request.Email,
            PasswordHash = request.PasswordHash,
            Salt = request.Salt,
            RoleId = (long)Roles.User,
            ScopeMembership = new ScopeUser { ScopeId = scope.Id },
            ScopeId = scope.Id
        };

        // NFR-23, as at every other creation of a User.
        LegalBasisRecorder.Record(person, scope, DateTime.UtcNow);

        var creation = await personWriter.CreateAsync(person);

        if (!creation.Success)
        {
            // Another instance took the address between the check above and this write.
            if (creation.Errors.Contains(RelationalErrors.UniqueViolationMessage))
            {
                await attemptSender.SendAsync(request.Email, googleAccount: false);
            }

            return;
        }

        await emailVerification.IssueAndSendAsync(person);
    }
}
