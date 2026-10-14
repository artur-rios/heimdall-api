using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Shared.Tests;

// Unit tests for ApplicationCredentialMessageMap (UC-53): each message the issue flow can return
// resolves to the status its flow names, so none ships answering the resolver's 400 default by accident.
public class ApplicationCredentialMessageMapTests
{
    [UnitFact]
    public void GivenTheIssueMessages_WhenResolved_ThenTheyCarryTheirFlowsStatuses()
    {
        // Given / When / Then — main flow 201; AF-53a 404; AF-53b 403; AF-53c 400; AF-53d 409
        var map = ApplicationCredentialMessageMap.StatusCodes;

        Assert.Equal(HttpStatusCodes.Created, map[ApplicationCredentialMessages.CredentialIssued]);
        Assert.Equal(HttpStatusCodes.NotFound, map[ApplicationMessages.ApplicationNotFound]);
        Assert.Equal(HttpStatusCodes.Forbidden, map[ApplicationCredentialMessages.NotAuthorizedToManageCredentials]);
        Assert.Equal(HttpStatusCodes.BadRequest, map[ApplicationCredentialMessages.ExpiryNotInFuture]);
        Assert.Equal(HttpStatusCodes.Conflict, map[ApplicationCredentialMessages.CredentialLimitReached]);
    }
}
