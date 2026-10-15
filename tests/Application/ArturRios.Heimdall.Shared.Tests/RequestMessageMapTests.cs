using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Shared.Tests;

// Unit tests for RequestMessageMap (NFR-10, #186): the shared malformed-request message resolves to 400
// rather than the resolver's default by accident.
public class RequestMessageMapTests
{
    [UnitFact]
    public void GivenTheMalformedRequestMessage_WhenResolved_ThenItIsBadRequest()
    {
        // Given / When / Then
        Assert.Equal(HttpStatusCodes.BadRequest, RequestMessageMap.StatusCodes[RequestMessages.Malformed]);
    }
}
