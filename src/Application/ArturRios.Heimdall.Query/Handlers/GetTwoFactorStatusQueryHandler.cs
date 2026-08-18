using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>
///     Handles <see cref="GetTwoFactorStatusQuery" /> (FR-2F-15): reports the caller's own two-factor
///     state — active or not, which methods are configured, and how many recovery codes remain.
/// </summary>
/// <remarks>
///     <para>
///         A caller who never enabled two-factor authentication is answered with every flag
///         <c>false</c> and a zero count, not with a refusal. That is the ordinary state of most
///         accounts, and a client's settings screen should not have to render its most common state
///         out of an error branch. <c>NotActive</c>'s 404 stays what UC-39 and UC-40 use it for:
///         refusing an operation that requires an active configuration.
///     </para>
///     <para>
///         A caller who is not an eligible person is refused with <c>NotEligible</c> (403), exactly
///         as UC-36's AF-36b refuses the same caller. <see cref="GoogleUser" /> and
///         <see cref="Person" /> are separate tables with separate <c>PublicId</c> spaces, so a
///         Google-issued token's subject never resolves here — and FR-2F-01 makes Google Users
///         permanently ineligible, which an all-false success would misreport as "off, and you could
///         turn it on". The same miss covers a token naming a person who no longer exists;
///         <c>ActorLivenessFilter</c> already answers 401 for that case before a request arrives, so
///         this branch is defence in depth.
///     </para>
/// </remarks>
public class GetTwoFactorStatusQueryHandler(
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncReadOnlyRepository<TwoFactorAuth> twoFactorReader,
    IAsyncReadOnlyRepository<TwoFactorRecoveryCode> recoveryCodeReader)
    : IQueryHandlerAsync<GetTwoFactorStatusQuery, TwoFactorStatusOutput>
{
    public async Task<DataOutput<TwoFactorStatusOutput?>> HandleAsync(GetTwoFactorStatusQuery query)
    {
        var output = DataOutput<TwoFactorStatusOutput?>.New;

        // AF-36b: the caller must be a live person. A Google User misses this lookup entirely.
        var personId = await personReader.Query()
            .Where(person => person.PublicId == query.ActingPersonId && !person.IsDeleted)
            .Select(person => (long?)person.Id)
            .FirstOrDefaultAsync();

        if (personId is null)
        {
            return output.WithError(TwoFactorMessages.NotEligible);
        }

        var configuration = await twoFactorReader.Query()
            .FirstOrDefaultAsync(x => x.PersonId == personId.Value);

        if (configuration is null)
        {
            return output
                .WithData(new TwoFactorStatusOutput())
                .WithMessage(TwoFactorMessages.StatusRetrieved);
        }

        var remainingRecoveryCodes = await recoveryCodeReader.Query()
            .CountAsync(code => code.TwoFactorAuthId == configuration.Id && !code.Used);

        return output
            .WithData(new TwoFactorStatusOutput
            {
                IsActive = configuration.IsActive,
                AppEnabled = configuration.AppEnabled,
                EmailEnabled = configuration.EmailEnabled,
                RemainingRecoveryCodes = remainingRecoveryCodes
            })
            .WithMessage(TwoFactorMessages.StatusRetrieved);
    }
}
