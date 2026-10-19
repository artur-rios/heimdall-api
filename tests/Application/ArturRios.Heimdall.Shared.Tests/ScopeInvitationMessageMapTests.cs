using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Shared.Tests;

// Unit tests for the scope owner invitation statuses (UC-21's invitation, UC-58…UC-61): each message
// resolves to the status its flow names, so none ships answering the resolver's 400 default by accident.
public class ScopeInvitationMessageMapTests
{
    [UnitFact]
    public void GivenTheNewMessages_WhenRead_ThenTheyAreTheSpecifiedStrings()
    {
        // Given
        // the four strings the CHANGELOG lists

        // When
        var sent = ScopeInvitationMessages.InvitationSent;
        var notOpen = ScopeInvitationMessages.InvitationNotOpen;
        var accepted = ScopeInvitationMessages.InvitationAccepted;
        var declined = ScopeInvitationMessages.InvitationDeclined;

        // Then
        Assert.Equal("The invitation was sent.", sent);
        Assert.Equal("The invitation was not found or is no longer open.", notOpen);
        Assert.Equal("The invitation was accepted.", accepted);
        Assert.Equal("The invitation was declined.", declined);
    }

    [UnitFact]
    public void GivenTheInvitationMessages_WhenResolved_ThenTheyCarryTheirFlowsStatuses()
    {
        // Given
        var map = ScopeInvitationMessageMap.StatusCodes;

        // When
        // accept and decline 200; not open 404; scope 404; not an owner 403
        var statuses = new[]
        {
            map[ScopeInvitationMessages.InvitationAccepted],
            map[ScopeInvitationMessages.InvitationDeclined],
            map[ScopeInvitationMessages.InvitationNotOpen],
            map[ScopeMessages.ScopeNotFound],
            map[ScopeMessages.NotScopeOwner],
            map[PaginationMessages.InvalidPageSize]
        };

        // Then
        Assert.Equal(
            [
                HttpStatusCodes.Ok, HttpStatusCodes.Ok, HttpStatusCodes.NotFound, HttpStatusCodes.NotFound,
                HttpStatusCodes.Forbidden, HttpStatusCodes.BadRequest
            ],
            statuses);
    }

    [UnitFact]
    public void GivenAScopeAdminsAdd_WhenItInvites_ThenTheAnswerIsAccepted()
    {
        // Given
        var map = PersonMessageMap.StatusCodes;

        // When
        // UC-21 for a Scope Admin answers 202, beside the unchanged 201 and 200
        var invited = map[ScopeInvitationMessages.InvitationSent];
        var added = map[PersonMessages.ScopeOwnerAddedSuccessfully];
        var already = map[PersonMessages.AlreadyScopeOwner];

        // Then
        Assert.Equal(HttpStatusCodes.Accepted, invited);
        Assert.Equal(HttpStatusCodes.Created, added);
        Assert.Equal(HttpStatusCodes.Ok, already);
    }
}
