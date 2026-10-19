using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;
using static ArturRios.Heimdall.WebApi.Tests.Support.ApplicationSeeds;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for GET and DELETE /api/scopes/{scopeId}/owner-invitations[/{invitationId}] (UC-58,
// FR-SC-15): an owner lists the open invitations and withdraws one (204, audited with the invitation as
// target); a processing-restricted invitee and a restricted inviter's name are withheld (NFR-24); a second
// withdrawal, another scope's invitation through this scope's route, and a non-owner are
// refused; a User 403 and an anonymous caller 401 by the attribute.
[Collection(nameof(FunctionalCollection))]
public class ScopeInvitationControllerScopeTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid scopeId) => $"/api/scopes/{scopeId}/owner-invitations";

    private static string Route(Guid scopeId, Guid invitationId) => $"{Route(scopeId)}/{invitationId}";

    private async Task<ScopeOwnerInvitation> SeedInvitationAsync(
        Scope scope, Person invitee, Person? inviter, DateTime? expiresAt = null, DateTime? endedAt = null)
    {
        await using var context = db.CreateContext();
        var invitation = new ScopeOwnerInvitation
        {
            PublicId = Guid.NewGuid(), ScopeId = scope.Id, InviteePersonId = invitee.Id,
            InvitedByPersonId = inviter?.Id, CreatedAt = DateTime.UtcNow.AddDays(-1),
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(6), EndedAt = endedAt,
            Outcome = endedAt is null ? null : ScopeOwnerInvitationOutcomes.Declined
        };
        context.ScopeOwnerInvitations.Add(invitation);
        await context.SaveChangesAsync();
        return invitation;
    }

    private async Task RestrictAsync(Person person)
    {
        await using var context = db.CreateContext();
        await context.Persons.Where(x => x.Id == person.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.ProcessingRestrictedAt, DateTime.UtcNow));
    }

    private async Task<ScopeOwnerInvitation> StoredAsync(ScopeOwnerInvitation invitation)
    {
        await using var context = db.CreateContext();
        return await context.ScopeOwnerInvitations.AsNoTracking().SingleAsync(x => x.Id == invitation.Id);
    }

    private Task<HttpOutput<PaginatedOutput<ScopeOwnerInvitationOutput>?>> ListAsync(Guid scopeId) =>
        Gateway.GetAsync<PaginatedOutput<ScopeOwnerInvitationOutput>>($"{Route(scopeId)}?pageNumber=1&pageSize=20");

    private Task<HttpOutput<DataOutput<object?>?>> WithdrawAsync(Guid scopeId, Guid invitationId) =>
        Gateway.DeleteAsync<DataOutput<object?>>(Route(scopeId, invitationId));

    [FunctionalFact]
    public async Task GivenAnOwner_WhenListing_ThenOnlyOpenInvitationsAreReturned()
    {
        // Given one open, one ended and one expired invitation
        var scope = await SeedScopeAsync(db);
        var owner = await SeedScopeAdminAsync(db, scope);
        var open = await SeedInvitationAsync(scope, await SeedScopeAdminAsync(db), owner);
        await SeedInvitationAsync(scope, await SeedScopeAdminAsync(db), owner, endedAt: DateTime.UtcNow);
        await SeedInvitationAsync(scope, await SeedScopeAdminAsync(db), owner, expiresAt: DateTime.UtcNow.AddMinutes(-1));
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await ListAsync(scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single(response.Body!.Data!);
        Assert.Equal(open.PublicId, item.Id);
        Assert.Equal(owner.Name, item.InvitedByName);
    }

    [FunctionalFact]
    public async Task GivenARestrictedInviteeAndARestrictedInviter_WhenListing_ThenTheirIdentitiesAreWithheld()
    {
        // Given NFR-24 — one invitation to a restricted invitee, one from a restricted inviter
        var scope = await SeedScopeAsync(db);
        var owner = await SeedScopeAdminAsync(db, scope);
        var restrictedInvitee = await SeedScopeAdminAsync(db);
        await SeedInvitationAsync(scope, restrictedInvitee, owner);
        await RestrictAsync(restrictedInvitee);
        var restrictedInviter = await SeedScopeAdminAsync(db, scope);
        var listed = await SeedInvitationAsync(scope, await SeedScopeAdminAsync(db), restrictedInviter);
        await RestrictAsync(restrictedInviter);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await ListAsync(scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body!.TotalItems);
        var item = Assert.Single(response.Body.Data!);
        Assert.Equal(listed.PublicId, item.Id);
        Assert.Null(item.InvitedByName);
    }

    [FunctionalFact]
    public async Task GivenAnOwner_WhenWithdrawing_ThenNoContentAndItEndsWithdrawnAndIsAudited()
    {
        // Given
        var scope = await SeedScopeAsync(db);
        var owner = await SeedScopeAdminAsync(db, scope);
        var invitation = await SeedInvitationAsync(scope, await SeedScopeAdminAsync(db), owner);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await WithdrawAsync(scope.PublicId, invitation.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Withdrawn, (await StoredAsync(invitation)).Outcome);
        await using var context = db.CreateContext();
        var entry = await context.AuditLogs.AsNoTracking().SingleAsync(x =>
            x.Action == nameof(WithdrawScopeOwnerInvitationCommand) && x.TargetId == invitation.PublicId);
        Assert.Equal(owner.PublicId, entry.ActorPersonId);
        Assert.True(entry.Succeeded);
    }

    [FunctionalFact]
    public async Task GivenAWithdrawnInvitation_WhenWithdrawnAgain_ThenNotFoundWithTheMessage()
    {
        // Given
        var scope = await SeedScopeAsync(db);
        var owner = await SeedScopeAdminAsync(db, scope);
        var invitation = await SeedInvitationAsync(scope, await SeedScopeAdminAsync(db), owner);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));
        await WithdrawAsync(scope.PublicId, invitation.PublicId);

        // When
        var response = await WithdrawAsync(scope.PublicId, invitation.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(ScopeInvitationMessages.InvitationNotOpen, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAnotherScopesInvitation_WhenWithdrawnThroughAScopeYouOwn_ThenNotFoundAndItStaysOpen()
    {
        // Given — Review Focus 3
        var scope = await SeedScopeAsync(db);
        var owner = await SeedScopeAdminAsync(db, scope);
        var otherScope = await SeedScopeAsync(db);
        var foreign = await SeedInvitationAsync(otherScope, await SeedScopeAdminAsync(db), null);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await WithdrawAsync(scope.PublicId, foreign.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null((await StoredAsync(foreign)).EndedAt);
    }

    [FunctionalFact]
    public async Task GivenAScopeAdminWhoDoesNotOwnTheScope_WhenListingOrWithdrawing_ThenForbidden()
    {
        // Given AF-58b
        var scope = await SeedScopeAsync(db);
        var stranger = await SeedScopeAdminAsync(db, await SeedScopeAsync(db));
        var invitation = await SeedInvitationAsync(scope, await SeedScopeAdminAsync(db), null);
        Authorize(TestTokens.For(stranger.PublicId, (int)Roles.ScopeAdmin));

        // When
        var list = await ListAsync(scope.PublicId);
        var withdraw = await WithdrawAsync(scope.PublicId, invitation.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, withdraw.StatusCode);
        Assert.Null((await StoredAsync(invitation)).EndedAt);
    }

    [FunctionalFact]
    public async Task GivenAUser_WhenListing_ThenForbiddenByTheAttribute()
    {
        // Given
        var scope = await SeedScopeAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await ListAsync(scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenListing_ThenUnauthorized()
    {
        // Given no bearer token
        var scope = await SeedScopeAsync(db);

        // When
        var response = await ListAsync(scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
