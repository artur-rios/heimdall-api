using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="SendPersonVerificationEmailCommand" /> (UC-51, FR-EV-06/07): an administrator
///     has a fresh verification link mailed to a person's own address.
/// </summary>
/// <remarks>
///     <para>
///         Refusals, in order: not found (AF-51a); the caller may not act on this person (AF-51b);
///         restricted, then deleted (AF-51c); already verified (AF-51d); the cooldown is running
///         (AF-51e). Authorization comes before the state checks, as UC-09's does, so a Scope Admin
///         learns nothing about persons outside their scopes.
///     </para>
///     <para>
///         The send itself is UC-15's: every live link is retired, then
///         <see cref="IEmailVerificationService" /> issues and mails a new one, so the link uses the
///         scope's <c>EmailVerificationUrl</c> exactly as self-service does. The token never appears
///         in the output or in a log. A Mailgun failure is swallowed by the sender as everywhere else,
///         so success means the email was handed over, not delivered.
///     </para>
/// </remarks>
public class SendPersonVerificationEmailCommandHandler(
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<EmailVerificationToken, long> tokenReader,
    IAsyncRepository<EmailVerificationToken, long> tokenWriter,
    IScopeOwnershipChecker scopeOwnership,
    IEmailVerificationService emailVerification,
    EmailVerificationOptions verificationOptions,
    AdminEmailOptions adminEmailOptions)
    : ICommandHandlerAsync<SendPersonVerificationEmailCommand, SendPersonVerificationEmailCommandOutput>
{
    public async Task<DataOutput<SendPersonVerificationEmailCommandOutput?>> HandleAsync(
        SendPersonVerificationEmailCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<SendPersonVerificationEmailCommandOutput?>.New;

        // AF-51a. No !IsDeleted filter: a logically deleted person is found and refused by AF-51c,
        // after authorization. A Google User's id misses — they are not persons.
        var person = await personReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.Id, cancellationToken);

        if (person is null)
        {
            return output.WithError(PersonMessages.PersonNotFound);
        }

        // AF-51b.
        if (!await PersonEmailAuthorization.ActorMayEmailAsync(
                scopeOwnership, command.ActingRole, command.ActingPersonId, person))
        {
            return output.WithError(PersonMessages.NotAuthorizedToEmailPerson);
        }

        // AF-51c. The restriction first, as UC-12/UC-52 check it (NFR-24).
        if (person.ProcessingRestrictedAt is not null)
        {
            return output.WithError(PersonMessages.PersonProcessingRestricted);
        }

        if (person.IsDeleted)
        {
            return output.WithError(PersonMessages.PersonIsDeleted);
        }

        // AF-51d.
        if (person.EmailVerified)
        {
            return output.WithError(AuthMessages.EmailAlreadyVerified);
        }

        // AF-51e. Any verification token issued for the person inside the window — used, superseded
        // or live, self-service or not — means one was sent recently.
        // Best-effort: concurrent requests for the same person can both pass this check (no lock).
        // Accepted, because only an authenticated administrator can trigger it.
        var now = DateTime.UtcNow;

        if (await adminEmailOptions.IsRunningAsync(
                tokenReader, person.Id, verificationOptions.TokenLifetime, now, cancellationToken))
        {
            return output.WithError(PersonMessages.AccountEmailSentRecently);
        }

        // As UC-15 step 3: retire every live link, so the new one is the only one that works.
        var retirement = await EmailVerificationTokenRetirement.RetireLiveAsync(
            tokenReader, tokenWriter, person.Id, now);

        if (retirement is not null)
        {
            return output.WithErrors(retirement);
        }

        await emailVerification.IssueAndSendAsync(person);

        return output
            .WithData(new SendPersonVerificationEmailCommandOutput { Id = person.PublicId })
            .WithMessage(PersonMessages.VerificationEmailSentToPerson);
    }
}
