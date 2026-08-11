using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="ResetPasswordCommand" /> (UC-13): finds the token UC-12 issued, refuses it
///     if it is unknown, expired, or spent (AF-13c/AF-13a/AF-13b, FR-PR-04), then re-hashes the new
///     password under a freshly generated salt and consumes the token (FR-PR-03).
/// </summary>
/// <remarks>
///     <para>
///         The token is the whole of the caller's claim — no email, no scope, no password is asked
///         for — so the three rejections are checked in the order UC-13's main flow states: exists,
///         not expired, not used. They are named separately, unlike UC-11's uniform 401, because
///         there is no account to enumerate here: the token is a 48-character random string, and a
///         caller either holds one that was mailed to them or holds nothing.
///     </para>
///     <para>
///         A person who has since been logically deleted, or whose scope has, still gets their
///         password changed. UC-12 will not issue them a token in the first place, so this only
///         arises when the deletion lands between the email and the click, and the new password
///         grants nothing regardless: UC-11 refuses the login anyway (AF-11c/AF-11d). Refusing here
///         would mean inventing an alternative flow the specification does not define.
///     </para>
/// </remarks>
public class ResetPasswordCommandHandler(
    IValidator<ResetPasswordCommand> validator,
    IAsyncReadOnlyRepository<PasswordResetToken> tokenReader,
    IAsyncRepository<PasswordResetToken> tokenWriter,
    IAsyncRepository<Person> personWriter)
    : ICommandHandlerAsync<ResetPasswordCommand, ResetPasswordCommandOutput>
{
    public async Task<DataOutput<ResetPasswordCommandOutput?>> HandleAsync(ResetPasswordCommand command)
    {
        var output = DataOutput<ResetPasswordCommandOutput?>.New;

        // AF-13d: validate input shape (NFR-10).
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        var now = DateTime.UtcNow;

        // UC-13 step 2. Matched exactly: the token is case-sensitive and uniquely indexed, so unlike
        // an email it is compared as issued.
        var token = await tokenReader.Query()
            .Include(x => x.Person)
            .FirstOrDefaultAsync(x => x.Token == command.Token);

        // AF-13c.
        if (token is null)
        {
            return output.WithError(AuthMessages.TokenInvalid);
        }

        // AF-13a.
        if (token.ExpiresAt <= now)
        {
            return output.WithError(AuthMessages.TokenExpired);
        }

        // AF-13b.
        if (token.Used)
        {
            return output.WithError(AuthMessages.TokenAlreadyUsed);
        }

        // UC-13 step 3 (FR-PR-03, FR-RO-04, NFR-02): a new random salt, not the person's existing
        // one, so the stored hash shares nothing with the one it replaces.
        var person = token.Person;

        person.PasswordHash = Hash.EncodeWithRandomSalt(command.NewPassword, out var salt);
        person.Salt = salt;
        person.UpdatedAt = now;

        var update = await personWriter.UpdateAsync(person);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        // UC-13 step 4.
        var consumption = await ConsumeTokensAsync(token, now);

        if (consumption is not null)
        {
            return output.WithErrors(consumption);
        }

        // UC-13 step 5.
        return output
            .WithData(new ResetPasswordCommandOutput())
            .WithMessage(AuthMessages.PasswordResetSuccessful);
    }

    /// <summary>
    ///     UC-13 step 4, applied to every reset token the person still holds rather than only the one
    ///     presented. UC-12 issues a token per request without retiring the last, so a person who
    ///     clicked "forgot password" twice has two live links. Once one of them has changed the
    ///     password, the others are exactly what this use case exists to expire: a way to change it
    ///     again, from an inbox that may be the reason the reset was needed.
    /// </summary>
    /// <remarks>
    ///     Already-expired tokens are left alone. They are refused by AF-13a either way, and
    ///     rewriting them would only make a dead token report a different reason for being dead.
    /// </remarks>
    private async Task<IEnumerable<string>?> ConsumeTokensAsync(PasswordResetToken token, DateTime now)
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
