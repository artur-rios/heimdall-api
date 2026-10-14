using System.Reflection;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Shared.Tests;

// Unit tests for AuditMessageMap (UC-48): every message the audit-trail listing can return resolves
// to the status its alternative flow names, so a new message cannot ship answering the resolver's
// 400 default by accident.
public class AuditMessageMapTests
{
    [UnitFact]
    public void GivenEveryAuditMessage_WhenLookedUpInTheMap_ThenEachHasAStatusCode()
    {
        // Given every canonical message AuditMessages declares
        var messages = typeof(AuditMessages)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        // When
        var unmapped = messages.Where(message => !AuditMessageMap.StatusCodes.ContainsKey(message)).ToList();

        // Then
        Assert.NotEmpty(messages);
        Assert.Empty(unmapped);
    }

    [UnitFact]
    public void GivenTheListingMessages_WhenResolved_ThenTheyCarryTheirFlowsStatuses()
    {
        // Given / When / Then — main flow and AF-48c answer 200; AF-48a answers 400
        Assert.Equal(HttpStatusCodes.Ok, AuditMessageMap.StatusCodes[AuditMessages.AuditLogRetrieved]);
        Assert.Equal(HttpStatusCodes.BadRequest, AuditMessageMap.StatusCodes[AuditMessages.InvalidDateRange]);
        Assert.Equal(HttpStatusCodes.BadRequest, AuditMessageMap.StatusCodes[PaginationMessages.FilterTooLong]);
        Assert.Equal(HttpStatusCodes.BadRequest, AuditMessageMap.StatusCodes[PaginationMessages.InvalidPageSize]);
        Assert.Equal(HttpStatusCodes.BadRequest, AuditMessageMap.StatusCodes[PaginationMessages.InvalidPageNumber]);
    }
}
