using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Shared.Tests;

// Unit tests for ApplicationCredentialMessageMap (UC-53…UC-55): each message the credential flows can return
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

    [UnitFact]
    public void GivenTheListAndRevokeMessages_WhenResolved_ThenTheyCarryTheirFlowsStatuses()
    {
        // Given / When / Then — UC-54 main flow 200; UC-55 main flow 200; AF-55c 404; AF-55d 409
        var map = ApplicationCredentialMessageMap.StatusCodes;

        Assert.Equal(HttpStatusCodes.Ok, map[ApplicationCredentialMessages.CredentialsRetrieved]);
        Assert.Equal(HttpStatusCodes.Ok, map[ApplicationCredentialMessages.CredentialRevoked]);
        Assert.Equal(HttpStatusCodes.NotFound, map[ApplicationCredentialMessages.CredentialNotFound]);
        Assert.Equal(HttpStatusCodes.Conflict, map[ApplicationCredentialMessages.CredentialAlreadyRevoked]);
    }
}
