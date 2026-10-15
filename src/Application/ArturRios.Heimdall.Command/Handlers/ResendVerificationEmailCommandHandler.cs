using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="ResendVerificationEmailCommand" /> (UC-15): refuses an address that is
///     already verified (AF-15a) and a request inside the per-person cooldown (AF-15b, FR-EV-07),
///     retires every verification link the caller still holds, and has a fresh one issued and mailed
///     (FR-EV-04).
/// </summary>
/// <remarks>
///     <para>
///         The counterpart of <see cref="VerifyEmailCommandHandler" />: that one spends a token, this
///         one replaces it. Issuing is not done here — <see cref="IEmailVerificationService" /> has
///         owned the token's length, alphabet, and lifetime since UC-06, and this handler decides only
///         whether and when to call it.
///     </para>
///     <para>
///         There is no input to validate and no authorization rule to enforce, because the request has
///         no body: the person comes from the bearer token, so the caller can only ever act on
///         themselves. That is also why the command needs no validator, as
///         <see cref="DeletePersonCommand" /> does not.
///     </para>
///     <para>
///         The cooldown is <see cref="AdminEmailOptions" />'s, asked exactly as UC-51 asks it: any
///         verification token issued for the person inside the window — by UC-06, UC-15 or UC-51,
///         used or not — means a link went out recently. Without it, whoever holds a token could loop
///         this endpoint and mail the account's address once per request; with it, one address gets
///         at most one link per cooldown however the request is repeated.
///     </para>
///     <para>
///         A person who has since been logically deleted is still served. UC-15 defines no
///         alternative flow for it, so a refusal would be one this handler invented, and a verified
///         address grants nothing on its own — UC-11 refuses the login by AF-11c regardless. This
///         deliberately differs from UC-12, which withholds a reset link from a deleted person: UC-12
///         must answer identically either way for anti-enumeration reasons, so withholding costs it
///         nothing, while here it would have to be a named error no document defines.
///     </para>
/// </remarks>
public class ResendVerificationEmailCommandHandler(
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<EmailVerificationToken, long> tokenReader,
    IAsyncRepository<EmailVerificationToken, long> tokenWriter,
    IEmailVerificationService emailVerification,
    EmailVerificationOptions verificationOptions,
    AdminEmailOptions emailOptions)
    : ICommandHandlerAsync<ResendVerificationEmailCommand, ResendVerificationEmailCommandOutput>
{
    public async Task<DataOutput<ResendVerificationEmailCommandOutput?>> HandleAsync(
        ResendVerificationEmailCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<ResendVerificationEmailCommandOutput?>.New;

        // UC-15 step 1. The lookup omits an !IsDeleted filter deliberately: a logically deleted person
        // is found and served, per the remarks above.
        var person = await personReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ActingPersonId);

        // Authentication runs in ClaimsOnly mode — no database read per request — so a valid bearer
        // token outlives the person it names once they are hard deleted (UC-10). The token is fine;
        // there is simply no address left to send to.
        if (person is null)
        {
            return output.WithError(AuthMessages.PersonNotFound);
        }

        // UC-15 step 2 (AF-15a). Checked before step 3, so a refused request retires nothing.
        if (person.EmailVerified)
        {
            return output.WithError(AuthMessages.EmailAlreadyVerified);
        }

        // AF-15b (FR-EV-07): a link went out within the cooldown. Also before step 3, so the link the
        // person already has keeps working. Best-effort as UC-51's is: two simultaneous requests can
        // both pass it, and the credential endpoints' rate limit bounds how many can.
        var now = DateTime.UtcNow;

        if (await emailOptions.IsRunningAsync(
                tokenReader, person.Id, verificationOptions.TokenLifetime, now, cancellationToken))
        {
            return output.WithError(AuthMessages.EmailSentRecently);
        }

        // UC-15 step 3: retire every live link, so the new one is the only one that works.
        var retirement = await EmailVerificationTokenRetirement.RetireLiveAsync(
            tokenReader, tokenWriter, person.Id, now);

        if (retirement is not null)
        {
            return output.WithErrors(retirement);
        }

        // UC-15 steps 4 and 5 (FR-EV-04). A delivery failure never reaches here: MailgunSender logs
        // and swallows it so UC-12's AF-12a cannot become an enumeration oracle. The token is
        // persisted before delivery is attempted, so a caller who receives nothing calls this endpoint
        // again — which is the whole point of the use case.
        await emailVerification.IssueAndSendAsync(person);

        // UC-15 step 6.
        return output
            .WithData(new ResendVerificationEmailCommandOutput())
            .WithMessage(AuthMessages.VerificationEmailSent);
    }
}
