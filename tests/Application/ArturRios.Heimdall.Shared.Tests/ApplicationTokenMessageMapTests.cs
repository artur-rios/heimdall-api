using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Shared.Tests;

// Unit tests for ApplicationTokenMessageMap (UC-56): each message the exchange can return resolves to
// the status its flow names, so none ships answering the resolver's 400 default by accident.
public class ApplicationTokenMessageMapTests
{
    [UnitFact]
    public void GivenTheExchangeMessages_WhenResolved_ThenTheyCarryTheirFlowsStatuses()
    {
        // Given / When / Then — main flow 200; AF-56a 400; AF-56b 401
        var map = ApplicationTokenMessageMap.StatusCodes;

        Assert.Equal(HttpStatusCodes.Ok, map[ApplicationTokenMessages.TokenIssued]);
        Assert.Equal(HttpStatusCodes.BadRequest, map[ApplicationTokenMessages.ApplicationIdRequired]);
        Assert.Equal(HttpStatusCodes.BadRequest, map[ApplicationTokenMessages.ScopeIdRequired]);
        Assert.Equal(HttpStatusCodes.BadRequest, map[ApplicationTokenMessages.SecretRequired]);
        Assert.Equal(HttpStatusCodes.BadRequest, map[ApplicationTokenMessages.SecretTooLong]);
        Assert.Equal(HttpStatusCodes.Unauthorized, map[ApplicationTokenMessages.CredentialsNotAccepted]);
    }
}
