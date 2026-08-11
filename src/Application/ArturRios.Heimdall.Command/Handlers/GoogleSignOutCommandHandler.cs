using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="GoogleSignOutCommand" /> (UC-26, FR-GO-18): confirms the caller's token
///     still names a Google User in good standing and acknowledges the sign-out; every other caller
///     is AF-26a. Nothing is written — see the remarks.
/// </summary>
/// <remarks>
///     <para>
///         UC-26 step 2 offers two ways to end a session — invalidate the token, or have the client
///         discard it — and defers to "the configured token strategy". This project's is the stateless
///         one UC-11 established: signed JWTs with an expiry (NFR-03), no refresh tokens and no
///         revocation list. So the sign-out has nothing to revoke, and the endpoint's job is to
///         confirm the caller is who UC-26 says may call it and answer the success that tells the
///         client to drop the token. Adding a denylist here would be a token strategy no document
///         chooses, and it would apply to every token the API issues, not just Google's.
///     </para>
///     <para>
///         That leaves the lookup as the whole of the endpoint's substance, and it is not ceremony:
///         authentication runs in <c>ClaimsOnly</c> mode — no database read per request — so a valid
///         bearer token outlives the Google User it names once UC-28 logically deletes or UC-29
///         removes them, exactly as UC-15's does for a person. A caller in that position is refused,
///         because UC-26's precondition is a live session issued by UC-25 and UC-25 itself would no
///         longer authenticate them (AF-25d).
///     </para>
///     <para>
///         Every refusal answers with <see cref="AuthMessages.GoogleAuthenticationFailed" />, the same
///         message UC-25 uses, so the endpoint cannot be used to tell a deleted Google User apart from
///         one that never existed.
///     </para>
/// </remarks>
public class GoogleSignOutCommandHandler(IAsyncReadOnlyRepository<GoogleUser> googleUserReader)
    : ICommandHandlerAsync<GoogleSignOutCommand, GoogleSignOutCommandOutput>
{
    public async Task<DataOutput<GoogleSignOutCommandOutput?>> HandleAsync(GoogleSignOutCommand command)
    {
        var output = DataOutput<GoogleSignOutCommandOutput?>.New;

        // UC-26 step 1 (AF-26a). The filter is part of the check rather than a separate branch: a
        // Google User that is missing and one that is logically deleted are refused alike, so the
        // answer distinguishes neither. The command carries no id of its own, so a caller can only
        // ever name themselves.
        var signedIn = await googleUserReader.Query()
            .AnyAsync(x => x.PublicId == command.ActingPersonId && !x.IsDeleted);

        if (!signedIn)
        {
            return output.WithError(AuthMessages.GoogleAuthenticationFailed);
        }

        // UC-26 steps 2 and 3: nothing to invalidate under the stateless strategy, so the success is
        // the instruction to discard the token.
        return output
            .WithData(new GoogleSignOutCommandOutput())
            .WithMessage(AuthMessages.GoogleSignOutSuccessful);
    }
}
