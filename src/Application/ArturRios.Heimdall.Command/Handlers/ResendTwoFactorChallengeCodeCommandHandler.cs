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
///     Handles <see cref="ResendTwoFactorChallengeCodeCommand" /> (UC-46, FR-2F-16): reissues the
///     email code for a challenge that is still outstanding, so someone who never received the first
///     one can ask for another without starting sign-in over.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every path answers the same.</b> A valid challenge, an unknown one, a forged one, an
///         expired one, one naming a person who no longer exists or is under a restriction, one
///         naming a person with no email method, and one that has spent its reissues all return
///         <see cref="TwoFactorMessages.ChallengeCodeResent" /> and no data. This is the endpoint's
///         security design rather than an economy of writing: it is anonymous, and an answer that
///         varied would say whether an address is registered and has email two-factor enabled — the
///         question UC-11's AF-11a…AF-11e and UC-38's AF-38a collapse their own answers to refuse.
///         The difference is only the sign: those collapse to one <c>401</c> because the caller asked
///         to be let in; this collapses to one <c>200</c> because they asked for something to be
///         sent, and "it has been sent if there was anything to send" is the most that can be said.
///     </para>
///     <para>
///         <b>It never extends the challenge.</b> Extending the window would mean returning a new
///         challenge token, and one could only be returned for a challenge that was real — exactly
///         the oracle above. So a reissue puts a fresh code inside the existing window and never
///         lengthens it. That window's ceiling is NFR-17's, which since the two lifetimes were
///         reconciled is the same ten minutes FR-2F-03 gives the code — which is what makes a reissue
///         inside it worth having rather than a formality.
///     </para>
///     <para>
///         <b>Why the reissues are capped.</b> FR-2F-13 retires an email code after five wrong
///         guesses. Without a cap this endpoint would relax that into "five guesses times however
///         many reissues the rate limiter permits", making a security bound a property of deployment
///         configuration. <see cref="MaxReissuesPerChallenge" /> puts it back in the requirement: at
///         most twenty guesses at a six-digit code per authentication, after which a further code
///         costs a fresh password check — which is the price FR-2F-13 always charged.
///     </para>
///     <para>
///         The count lives on the <c>TWO_FACTOR_AUTH</c> row rather than in a token claim, because a
///         claim could only be incremented by issuing a new token and this endpoint must never return
///         one. <c>LoginCommandHandler</c> clears it whenever it issues a challenge, which is what
///         makes the budget per-challenge rather than per-account.
///     </para>
/// </remarks>
public class ResendTwoFactorChallengeCodeCommandHandler(
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncReadOnlyRepository<TwoFactorAuth> twoFactorReader,
    IAsyncRepository<TwoFactorAuth> twoFactorWriter,
    ITwoFactorChallengeTokenValidator challengeTokenValidator,
    ITwoFactorEmailCodeIssuer emailCodeIssuer)
    : ICommandHandlerAsync<ResendTwoFactorChallengeCodeCommand, ResendTwoFactorChallengeCodeCommandOutput>
{
    /// <summary>
    ///     Reissues one challenge may authorize (FR-2F-13). Three, so the guessing bound stays a
    ///     stated constant — five attempts per code across at most four codes — rather than whatever
    ///     the rate limiter happens to permit.
    /// </summary>
    public const int MaxReissuesPerChallenge = 3;

    public async Task<DataOutput<ResendTwoFactorChallengeCodeCommandOutput?>> HandleAsync(
        ResendTwoFactorChallengeCodeCommand command)
    {
        // UC-46 steps 2-6. Resolved before the answer is built, but the answer does not depend on
        // it: each alternative flow is the absence of work rather than a refusal of its own, the
        // same way UC-12's AF-12a is.
        var reissuable = await ResolveReissuableAsync(command.ChallengeToken);

        if (reissuable is { } target)
        {
            await ReissueAsync(target.Configuration, target.Email);
        }

        // UC-46 step 7: the same answer either way.
        return DataOutput<ResendTwoFactorChallengeCodeCommandOutput?>.New
            .WithData(new ResendTwoFactorChallengeCodeCommandOutput())
            .WithMessage(TwoFactorMessages.ChallengeCodeResent);
    }

    /// <summary>
    ///     Charges one reissue against the challenge, then sends. The budget is spent before the
    ///     code goes out, and a failure to spend it stops the send: overcharging costs a person one
    ///     more wait, while undercharging would let the bound be stepped around by whatever made the
    ///     write fail. Neither outcome reaches the caller.
    /// </summary>
    private async Task ReissueAsync(TwoFactorAuth twoFactorAuth, string email)
    {
        twoFactorAuth.EmailCodeReissueCount++;

        var budget = await twoFactorWriter.UpdateAsync(twoFactorAuth);

        if (!budget.Success)
        {
            return;
        }

        await emailCodeIssuer.ReissueAsync(twoFactorAuth, email);
    }

    /// <summary>
    ///     The configuration a reissue should be sent for and the address to send it to, or
    ///     <see langword="null" /> when there is nothing to send: the challenge did not validate
    ///     (AF-46a/AF-46e), it names nobody eligible (AF-46b), that person has no active
    ///     configuration or no email method (AF-46c), or its reissues are spent (AF-46d). The caller
    ///     cannot tell these apart, and neither can this method's result.
    /// </summary>
    private async Task<(TwoFactorAuth Configuration, string Email)?> ResolveReissuableAsync(
        string? challengeToken)
    {
        // AF-46a: signature, expiry, and the MFA-pending claim — exactly UC-38 step 2's check, made
        // by the same validator, so the two endpoints agree on what a challenge token is. AF-46e
        // falls out of the same call: a full login token carries no MFA-pending claim.
        var principal = await challengeTokenValidator.ValidateAsync(challengeToken);

        if (principal is null)
        {
            return null;
        }

        var person = await personReader.Query()
            .FirstOrDefaultAsync(person => person.PublicId == principal.PersonId && !person.IsDeleted);

        // AF-46b. The restriction check mirrors UC-12's: sending to a restricted identity would be
        // processing it (NFR-24), and a person who acquired the restriction after UC-11 issued their
        // challenge could otherwise still be mailed.
        if (person is null || person.ProcessingRestrictedAt is not null)
        {
            return null;
        }

        var twoFactorAuth = await twoFactorReader.Query()
            .FirstOrDefaultAsync(configuration =>
                configuration.PersonId == person.Id && configuration.IsActive);

        // AF-46c — no active configuration, or one without the email method: an authenticator-app
        // holder has no code to resend. AF-46d — the challenge has spent its reissues.
        return twoFactorAuth is { EmailEnabled: true } &&
               twoFactorAuth.EmailCodeReissueCount < MaxReissuesPerChallenge
            // The address is the person's stored one, read here rather than taken from the request:
            // the command carries no address field, and this is why.
            ? (twoFactorAuth, person.Email)
            : null;
    }
}
