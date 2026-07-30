using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Command.Services;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Command.Handlers;

/// <summary>
///     Handles <see cref="ResendVerificationEmailCommand" /> (UC-15): refuses an address that is
///     already verified (AF-15a), retires every verification link the caller still holds, and has a
///     fresh one issued and mailed (FR-EV-04).
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
///         A person who has since been logically deleted is still served. UC-15 defines exactly one
///         alternative flow, so a second refusal would be one this handler invented, and a verified
///         address grants nothing on its own — UC-11 refuses the login by AF-11c regardless. This
///         deliberately differs from UC-12, which withholds a reset link from a deleted person: UC-12
///         must answer identically either way for anti-enumeration reasons, so withholding costs it
///         nothing, while here it would have to be a named error no document defines.
///     </para>
/// </remarks>
public class ResendVerificationEmailCommandHandler(
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncReadOnlyRepository<EmailVerificationToken> tokenReader,
    IAsyncRepository<EmailVerificationToken> tokenWriter,
    IEmailVerificationService emailVerification)
    : ICommandHandlerAsync<ResendVerificationEmailCommand, ResendVerificationEmailCommandOutput>
{
    public async Task<DataOutput<ResendVerificationEmailCommandOutput?>> HandleAsync(
        ResendVerificationEmailCommand command)
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

        // UC-15 step 3.
        var retirement = await RetireTokensAsync(person, DateTime.UtcNow);

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

    /// <summary>
    ///     UC-15 step 3: retires every verification token the person still holds, so that once a new
    ///     link is mailed it is the only one that works.
    /// </summary>
    /// <remarks>
    ///     Same shape and same boundaries as <c>VerifyEmailCommandHandler</c>'s consumption pass:
    ///     already-expired tokens are left alone — AF-14a refuses them either way, and rewriting them
    ///     would only make a dead token report a different reason for being dead — and another person's
    ///     tokens are never touched.
    /// </remarks>
    private async Task<IEnumerable<string>?> RetireTokensAsync(Person person, DateTime now)
    {
        var live = await tokenReader.Query()
            .Where(x => x.PersonId == person.Id && !x.Used && x.ExpiresAt > now)
            .ToListAsync();

        foreach (var outstanding in live)
        {
            outstanding.Used = true;

            var update = await tokenWriter.UpdateAsync(outstanding);

            if (!update.Success)
            {
                return update.Errors;
            }
        }

        return null;
    }
}
