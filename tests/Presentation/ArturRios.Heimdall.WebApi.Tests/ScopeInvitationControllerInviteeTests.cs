using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Data.Persistence;
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
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using static ArturRios.Heimdall.WebApi.Tests.Support.ApplicationSeeds;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for the invitee's side (UC-59…UC-61, FR-SC-16, FR-SC-17): the whole flow from a Scope
// Admin's invite to the invitee owning the scope and seeing it listed; the inviter and an outsider refused
// like an unknown id; a decline, audited; an invitee who already owns the scope; a withdrawal committed after
// the accept read the invitation; two accepts at once; an invitee demoted between the accept's read and its
// write, which voids it; a deleted scope's invitation hidden; an inviter hard-deleted meanwhile; a User refused.
[Collection(nameof(FunctionalCollection))]
public class ScopeInvitationControllerInviteeTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Mine = "/api/persons/me/scope-invitations?pageNumber=1&pageSize=20";

    private static string Accept(Guid invitationId) => $"/api/scope-invitations/{invitationId}/accept";

    private static string Decline(Guid invitationId) => $"/api/scope-invitations/{invitationId}/decline";

    private async Task<ScopeOwnerInvitation> SeedInvitationAsync(Scope scope, Person invitee, Person? inviter)
    {
        await using var context = db.CreateContext();
        var invitation = new ScopeOwnerInvitation
        {
            PublicId = Guid.NewGuid(), ScopeId = scope.Id, InviteePersonId = invitee.Id,
            InvitedByPersonId = inviter?.Id, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        context.ScopeOwnerInvitations.Add(invitation);
        await context.SaveChangesAsync();
        return invitation;
    }

    private async Task<ScopeOwnerInvitation> StoredAsync(ScopeOwnerInvitation invitation)
    {
        await using var context = db.CreateContext();
        return await context.ScopeOwnerInvitations.AsNoTracking().SingleAsync(x => x.Id == invitation.Id);
    }

    private async Task<int> OwnershipCountAsync(Scope scope, Person person)
    {
        await using var context = db.CreateContext();
        return await context.ScopeOwners.CountAsync(x => x.ScopeId == scope.Id && x.PersonId == person.Id);
    }

    private async Task<Exception?> RunAsync(string statement)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(statement, connection);
        return await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync());
    }

    private Task<HttpOutput<DataOutput<AcceptScopeOwnerInvitationCommandOutput?>?>> AcceptAsync(Guid invitationId) =>
        Gateway.PostAsync<DataOutput<AcceptScopeOwnerInvitationCommandOutput?>>(Accept(invitationId), new { });

    [FunctionalFact]
    public async Task GivenAScopeAdminsInvitation_WhenTheInviteeAcceptsIt_ThenTheyOwnTheScopeAndSeeItListed()
    {
        // Given — an owner invites an existing Scope Admin (UC-21)
        var scope = await Seeds.ScopeAsync(db);
        var owner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin", Owns = [scope] });
        var invitee = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" });
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));
        var invite = await Gateway.PostAsync<DataOutput<AddScopeOwnerCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/owners/{invitee.PublicId}", new { });
        Assert.Equal(HttpStatusCode.Accepted, invite.StatusCode);

        // When — the invitee, signed in as themselves, lists and accepts it (UC-59, UC-60)
        Authorize(TestTokens.For(invitee.PublicId, (int)Roles.ScopeAdmin));
        var mine = await Gateway.GetAsync<PaginatedOutput<MyScopeInvitationOutput>>(Mine);
        var listed = Assert.Single(mine.Body!.Data!);
        var accepted = await AcceptAsync(listed.Id);

        // Then — accepted, owning, and the scope is in their list at once
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal(scope.Name, listed.ScopeName);
        Assert.Equal(owner.Name, listed.InvitedByName);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Contains(ScopeInvitationMessages.InvitationAccepted, accepted.Body!.Messages);
        Assert.Equal(scope.PublicId, accepted.Body.Data!.ScopeId);
        Assert.Equal(1, await OwnershipCountAsync(scope, invitee));

        var scopes = await Gateway.GetAsync<PaginatedOutput<ScopeOutput>>("/api/scopes?pageNumber=1&pageSize=100");
        Assert.Contains(scopes.Body!.Data!, item => item.Id == scope.PublicId);

        // Then — the scope side lists nothing open, and the acceptance is audited with the invitation as target
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));
        var open = await Gateway.GetAsync<PaginatedOutput<ScopeOwnerInvitationOutput>>(
            $"/api/scopes/{scope.PublicId}/owner-invitations?pageNumber=1&pageSize=20");
        Assert.Empty(open.Body!.Data!);
        await using var context = db.CreateContext();
        var entry = await context.AuditLogs.AsNoTracking().SingleAsync(x =>
            x.Action == nameof(AcceptScopeOwnerInvitationCommand) && x.TargetId == listed.Id);
        Assert.Equal(invitee.PublicId, entry.ActorPersonId);
    }

    [FunctionalFact]
    public async Task GivenTheInviterOrAnOutsider_WhenAccepting_ThenNotFoundAndItStaysOpen()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db);
        var inviter = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin", Owns = [scope] });
        var invitation = await SeedInvitationAsync(scope, await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" }), inviter);
        var outsider = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" });

        // When
        Authorize(TestTokens.For(inviter.PublicId, (int)Roles.ScopeAdmin));
        var byInviter = await AcceptAsync(invitation.PublicId);
        Authorize(TestTokens.For(outsider.PublicId, (int)Roles.ScopeAdmin));
        var byOutsider = await AcceptAsync(invitation.PublicId);

        // Then — the same answer as an unknown id
        Assert.Equal(HttpStatusCode.NotFound, byInviter.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byOutsider.StatusCode);
        Assert.Contains(ScopeInvitationMessages.InvitationNotOpen, byInviter.Body!.Errors);
        Assert.Contains(ScopeInvitationMessages.InvitationNotOpen, byOutsider.Body!.Errors);
        Assert.Null((await StoredAsync(invitation)).EndedAt);
    }

    [FunctionalFact]
    public async Task GivenAnOpenInvitation_WhenTheInviteeDeclines_ThenItEndsDeclinedWithNoOwnership()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db);
        var invitee = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" });
        var invitation = await SeedInvitationAsync(scope, invitee, await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin", Owns = [scope] }));
        Authorize(TestTokens.For(invitee.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<DeclineScopeOwnerInvitationCommandOutput?>>(
            Decline(invitation.PublicId), new { });

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(ScopeInvitationMessages.InvitationDeclined, response.Body!.Messages);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Declined, (await StoredAsync(invitation)).Outcome);
        Assert.Equal(0, await OwnershipCountAsync(scope, invitee));

        // Then — the decline is audited under its command's name with the invitation as target
        await using var context = db.CreateContext();
        var entry = await context.AuditLogs.AsNoTracking().SingleAsync(x =>
            x.Action == nameof(DeclineScopeOwnerInvitationCommand) && x.TargetId == invitation.PublicId);
        Assert.True(entry.Succeeded);
        Assert.Equal(invitee.PublicId, entry.ActorPersonId);
    }

    [FunctionalFact]
    public async Task GivenAnInviteeWhoAlreadyOwnsTheScope_WhenAccepting_ThenAcceptedWithOneOwnership()
    {
        // Given — the ownership was granted meanwhile, outside the invitation (e.g. a System Admin's UC-21)
        var scope = await Seeds.ScopeAsync(db);
        var invitee = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" });
        var invitation = await SeedInvitationAsync(scope, invitee, await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin", Owns = [scope] }));
        Assert.Null(await RunAsync(
            $"INSERT INTO heimdall.scope_owner (scope_id, person_id) VALUES ({scope.Id}, {invitee.Id})"));
        Authorize(TestTokens.For(invitee.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await AcceptAsync(invitation.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(ScopeInvitationMessages.InvitationAccepted, response.Body!.Messages);
        Assert.Equal(1, await OwnershipCountAsync(scope, invitee));
        Assert.Equal(ScopeOwnerInvitationOutcomes.Accepted, (await StoredAsync(invitation)).Outcome);
    }

    [FunctionalFact]
    public async Task GivenAWithdrawalCommittedAfterTheAcceptReadIt_WhenAccepting_ThenNotOpenAndTheWithdrawalStands()
    {
        // Given — the accept/withdraw race, made deterministic: the withdrawal commits just before the
        // accepting context's first UPDATE (the shared Support/BeforeFirstUpdate)
        var scope = await Seeds.ScopeAsync(db);
        var invitee = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" });
        var invitation = await SeedInvitationAsync(scope, invitee, await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin", Owns = [scope] }));
        await using var context = db.CreateContext(new BeforeFirstUpdate(() => WithdrawAsync(invitation)));
        var audit = new RecordingAudit();
        var handler = new AcceptScopeOwnerInvitationCommandHandler(
            new EfRepository<Person, long>(context), new EfRepository<Scope, long>(context),
            new EfRepository<ScopeOwnerInvitation, long>(context), new ScopeOwnerInvitations(context),
            new SilentNotices(), audit, NullLogger<AcceptScopeOwnerInvitationCommandHandler>.Instance);

        // When
        var output = await handler.HandleAsync(new AcceptScopeOwnerInvitationCommand
        {
            InvitationId = invitation.PublicId, ActingPersonId = invitee.PublicId, ActingRole = (int)Roles.ScopeAdmin
        });

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Withdrawn, (await StoredAsync(invitation)).Outcome);
        Assert.Equal(0, await OwnershipCountAsync(scope, invitee));
        Assert.Empty(audit.Entries);
    }

    [FunctionalFact]
    public async Task GivenTheInviteeDemotedAfterTheAcceptReadThem_WhenAccepting_ThenVoidedAndTheVoidIsAudited()
    {
        // Given — AF-60b landing between the handler's check and its write (G-F28): the invitee becomes a User
        // just before the accepting context's first UPDATE, which then refuses the acceptance
        var scope = await Seeds.ScopeAsync(db);
        var invitee = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" });
        var invitation = await SeedInvitationAsync(scope, invitee, null);
        await using var context = db.CreateContext(new BeforeFirstUpdate(async () =>
            Assert.Null(await RunAsync(
                $"UPDATE heimdall.person SET role_id = {(int)Roles.User} WHERE id = {invitee.Id}"))));
        var audit = new RecordingAudit();
        var handler = new AcceptScopeOwnerInvitationCommandHandler(
            new EfRepository<Person, long>(context), new EfRepository<Scope, long>(context),
            new EfRepository<ScopeOwnerInvitation, long>(context), new ScopeOwnerInvitations(context),
            new SilentNotices(), audit, NullLogger<AcceptScopeOwnerInvitationCommandHandler>.Instance);

        // When
        var output = await handler.HandleAsync(new AcceptScopeOwnerInvitationCommand
        {
            InvitationId = invitation.PublicId, ActingPersonId = invitee.PublicId, ActingRole = (int)Roles.ScopeAdmin
        });

        // Then
        Assert.Equal([ScopeInvitationMessages.InvitationNotOpen], output.Errors);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Voided, (await StoredAsync(invitation)).Outcome);
        Assert.Equal(0, await OwnershipCountAsync(scope, invitee));
        Assert.Equal([(ScopeOwnerInvitationAuditActions.Voided, (Guid?)invitation.PublicId)], audit.Entries);
    }

    [FunctionalFact]
    public async Task GivenTwoAcceptsAtOnce_WhenBothArrive_ThenExactlyOneSucceeds()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db);
        var invitee = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" });
        var invitation = await SeedInvitationAsync(scope, invitee, null);
        Authorize(TestTokens.For(invitee.PublicId, (int)Roles.ScopeAdmin));

        // When
        var responses = await Task.WhenAll(AcceptAsync(invitation.PublicId), AcceptAsync(invitation.PublicId));

        // Then
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NotFound);
        Assert.Equal(1, await OwnershipCountAsync(scope, invitee));
    }

    [FunctionalFact]
    public async Task GivenAnInvitationToADeletedScope_WhenListingMine_ThenItIsHidden()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db);
        var invitee = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" });
        await SeedInvitationAsync(scope, invitee, null);
        Assert.Null(await RunAsync($"UPDATE heimdall.scope SET is_deleted = true WHERE id = {scope.Id}"));
        Authorize(TestTokens.For(invitee.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<MyScopeInvitationOutput>>(Mine);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Body!.Data!);
    }

    [FunctionalFact]
    public async Task GivenTheInviterHardDeleted_WhenTheInviteeAccepts_ThenAccepted()
    {
        // Given — Review Focus 4
        var scope = await Seeds.ScopeAsync(db);
        var inviter = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin", Owns = [scope] });
        var invitee = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" });
        var invitation = await SeedInvitationAsync(scope, invitee, inviter);
        Assert.Null(await RunAsync($"DELETE FROM heimdall.person WHERE id = {inviter.Id}"));
        Authorize(TestTokens.For(invitee.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await AcceptAsync(invitation.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await OwnershipCountAsync(scope, invitee));
    }

    [FunctionalFact]
    public async Task GivenAUser_WhenListingMine_ThenForbidden()
    {
        // Given
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<MyScopeInvitationOutput>>(Mine);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>The competing owner's withdrawal: its own connection, committed when this returns.</summary>
    private async Task WithdrawAsync(ScopeOwnerInvitation invitation)
    {
        await using var other = db.CreateContext();
        await other.ScopeOwnerInvitations
            .Where(x => x.Id == invitation.Id)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.EndedAt, DateTime.UtcNow)
                .SetProperty(x => x.Outcome, ScopeOwnerInvitationOutcomes.Withdrawn));
    }

    private sealed class SilentNotices : IScopeInvitationNoticeSender
    {
        public Task SendInvitationAsync(string inviteeEmail, string inviterName, string scopeName, DateTime expiresAt) =>
            Task.CompletedTask;

        public Task SendAnswerAsync(string inviterEmail, string inviteeName, string scopeName, bool accepted) =>
            Task.CompletedTask;
    }

    /// <summary>Records the entries the handler writes itself; the decorator is not in play here.</summary>
    private sealed class RecordingAudit : IAuditLogWriter
    {
        public List<(string Action, Guid? TargetId)> Entries { get; } = [];

        public Task WriteAsync(string action, Guid? targetId, bool succeeded, string? failureReason)
        {
            Entries.Add((action, targetId));

            return Task.CompletedTask;
        }
    }
}
