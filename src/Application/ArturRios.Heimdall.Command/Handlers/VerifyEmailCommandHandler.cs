using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="VerifyEmailCommand" /> (UC-14): finds the token UC-06 issued at person
///     creation, refuses it if it is unknown, expired, or spent (AF-14c/AF-14a/AF-14b), then marks the
///     person's address as verified and consumes the token (FR-EV-03).
/// </summary>
/// <remarks>
///     <para>
///         The mirror of <see cref="ResetPasswordCommandHandler" />, and it takes the same positions
///         for the same reasons. The token is the whole of the caller's claim — no email, no scope —
///         so the three rejections are checked in the order UC-14's main flow states: exists, not
///         expired, not used. They are named separately, unlike UC-11's uniform 401, because there is
///         no account to enumerate here: the token is a 48-character random string, and a caller
///         either holds one that was mailed to them or holds nothing.
///     </para>
///     <para>
///         An address that is already verified is verified again, and the token spent. UC-14 defines
///         no alternative flow for it — AF-15a rejects a *request* for another verification email, a
///         different thing — so refusing here would mean inventing one. Likewise a person who has
///         since been logically deleted: UC-06 will not have issued a token to someone already gone,
///         so this only arises when the deletion lands between the email and the click, and a verified
///         address grants nothing regardless, since UC-11 refuses the login anyway (AF-11c).
///     </para>
/// </remarks>
public class VerifyEmailCommandHandler(
    IValidator<VerifyEmailCommand> validator,
    IAsyncReadOnlyRepository<EmailVerificationToken> tokenReader,
    IAsyncRepository<EmailVerificationToken> tokenWriter,
    IAsyncRepository<Person> personWriter)
    : ICommandHandlerAsync<VerifyEmailCommand, VerifyEmailCommandOutput>
{
    public async Task<DataOutput<VerifyEmailCommandOutput?>> HandleAsync(VerifyEmailCommand command)
    {
        var output = DataOutput<VerifyEmailCommandOutput?>.New;

        // Validate input shape (NFR-10). UC-14 names no alternative flow for it; an absent token
        // would answer 400 as AF-14c anyway.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        var now = DateTime.UtcNow;

        // UC-14 step 2. Matched by hash rather than by value, for the reason given in
        // ResetPasswordCommandHandler and in SingleUseTokenHash: the token is not stored.
        var presented = SingleUseTokenHash.Of(command.Token);

        var token = await tokenReader.Query()
            .Include(x => x.Person)
            .FirstOrDefaultAsync(x => x.TokenHash == presented);

        // AF-14c.
        if (token is null)
        {
            return output.WithError(AuthMessages.TokenInvalid);
        }

        // AF-14a.
        if (token.ExpiresAt <= now)
        {
            return output.WithError(AuthMessages.TokenExpired);
        }

        // AF-14b.
        if (token.Used)
        {
            return output.WithError(AuthMessages.TokenAlreadyUsed);
        }

        // UC-14 step 3 (FR-EV-03).
        var person = token.Person;

        person.EmailVerified = true;
        person.UpdatedAt = now;

        var update = await personWriter.UpdateAsync(person);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        // UC-14 step 4.
        var consumption = await ConsumeTokensAsync(token, now);

        if (consumption is not null)
        {
            return output.WithErrors(consumption);
        }

        // UC-14 step 5.
        return output
            .WithData(new VerifyEmailCommandOutput())
            .WithMessage(AuthMessages.EmailVerifiedSuccessfully);
    }

    /// <summary>
    ///     UC-14 step 4, applied to every verification token the person still holds rather than only
    ///     the one presented. UC-06 issues one at creation and UC-15 issues more on request, so a
    ///     person can hold several live links; once one of them has verified the address, the rest
    ///     verify an address that is already verified and are worth nothing but the room they take up
    ///     in a mailbox.
    /// </summary>
    /// <remarks>
    ///     This is not UC-15 step 3, which retires outstanding tokens before issuing a new one. That
    ///     remains UC-15's own work.
    ///     <para>
    ///         Already-expired tokens are left alone. They are refused by AF-14a either way, and
    ///         rewriting them would only make a dead token report a different reason for being dead.
    ///     </para>
    /// </remarks>
    private async Task<IEnumerable<string>?> ConsumeTokensAsync(EmailVerificationToken token, DateTime now)
    {
        var live = await tokenReader.Query()
            .Where(x => x.PersonId == token.PersonId && !x.Used && x.ExpiresAt > now)
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
