using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Shared.Tests;

// Unit tests for DataAccessMessageMap and its effect on every use case's status map.
//
// The persistence layer classifies a provider failure into one of RelationalErrors' fixed messages
// (Relational.Core 4.0.0). Those strings are the only stable thing a status can be keyed off — the
// provider text they replaced named indexes, columns and conflicting values, so it could not be
// mapped and fell through to the resolver's 400 default. These tests pin both halves: that each
// classified failure now carries a status describing it, and that folding them in did not disturb
// any use case's own vocabulary.
public class DataAccessMessageMapTests
{
    public static TheoryData<string, IReadOnlyDictionary<string, int>> EveryUseCaseMap() => new()
    {
        { nameof(ApplicationMessageMap), ApplicationMessageMap.StatusCodes },
        { nameof(AuthMessageMap), AuthMessageMap.StatusCodes },
        { nameof(GoogleUserMessageMap), GoogleUserMessageMap.StatusCodes },
        { nameof(PersonMessageMap), PersonMessageMap.StatusCodes },
        { nameof(ScopeMessageMap), ScopeMessageMap.StatusCodes },
        { nameof(ScopePermissionMessageMap), ScopePermissionMessageMap.StatusCodes },
        { nameof(TwoFactorMessageMap), TwoFactorMessageMap.StatusCodes }
    };

    [UnitTheory]
    [MemberData(nameof(EveryUseCaseMap))]
    public void GivenAnyUseCaseMap_WhenResolvingAClassifiedFailure_ThenItCarriesADescribingStatus(
        string mapName, IReadOnlyDictionary<string, int> statusCodes)
    {
        // Every controller passes one of these maps, so a repository failure has to resolve the same
        // way whichever endpoint it surfaced through.
        Assert.Equal(
            HttpStatusCodes.Conflict, statusCodes[RelationalErrors.UniqueViolationMessage]);
        Assert.Equal(
            HttpStatusCodes.Conflict, statusCodes[RelationalErrors.IntegrityViolationMessage]);
        Assert.Equal(
            HttpStatusCodes.Conflict, statusCodes[RelationalErrors.ConcurrencyMessage]);
        Assert.Equal(
            HttpStatusCodes.ServiceUnavailable, statusCodes[RelationalErrors.TransientMessage]);

        Assert.False(string.IsNullOrWhiteSpace(mapName));
    }

    [UnitTheory]
    [MemberData(nameof(EveryUseCaseMap))]
    public void GivenAnyUseCaseMap_WhenResolvingAnUnclassifiedFailure_ThenItIsLeftToTheDefault(
        string mapName, IReadOnlyDictionary<string, int> statusCodes)
    {
        // GenericMessage covers everything the library could not place — both causes a caller can
        // fix and causes only an operator can — so it is deliberately unmapped and keeps the
        // resolver's 400. Pinned because mapping it later is a decision, not a tidy-up.
        Assert.DoesNotContain(RelationalErrors.GenericMessage, statusCodes.Keys);

        Assert.False(string.IsNullOrWhiteSpace(mapName));
    }

    [UnitFact]
    public void GivenAUseCaseMessage_WhenCombining_ThenTheUseCaseKeepsItsOwnStatus()
    {
        // A use case owns its vocabulary; the shared entries must never be able to override it.
        Assert.Equal(HttpStatusCodes.Unauthorized, AuthMessageMap.StatusCodes[AuthMessages.InvalidCredentials]);
        Assert.Equal(HttpStatusCodes.Conflict, AuthMessageMap.StatusCodes[AuthMessages.EmailAlreadyExists]);
        Assert.Equal(HttpStatusCodes.NotFound, PersonMessageMap.StatusCodes[PersonMessages.PersonNotFound]);
    }

    [UnitFact]
    public void GivenACollidingUseCaseMessage_WhenCombining_ThenTheUseCaseWins()
    {
        // The collision cannot happen with today's vocabulary — the library's messages are phrased
        // unlike any of this application's — but the precedence is the contract, so it is pinned
        // rather than left to be discovered the day one collides.
        var combined = DataAccessMessageMap.CombinedWith(new Dictionary<string, int>
        {
            [RelationalErrors.UniqueViolationMessage] = HttpStatusCodes.BadRequest
        });

        Assert.Equal(HttpStatusCodes.BadRequest, combined[RelationalErrors.UniqueViolationMessage]);
        Assert.Equal(HttpStatusCodes.ServiceUnavailable, combined[RelationalErrors.TransientMessage]);
    }
}
