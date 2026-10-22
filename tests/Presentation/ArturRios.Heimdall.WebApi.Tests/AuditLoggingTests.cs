using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for NFR-09 (audit logging): every attempted write produces one AuditLog row
// carrying the acting caller's identity, whether the API allowed it or refused it.
//
// The refusal half used to assert the opposite — that a rejected command wrote nothing. That left
// the trail describing only what the API permitted, which is the wrong half to keep: a write that
// succeeded is visible in the data it produced, while a caller repeatedly denied a scope they do not
// own leaves no trace anywhere else.
[Collection(nameof(FunctionalCollection))]
public class AuditLoggingTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static CreateApplicationCommand Command(Guid ownerId, string? name = null) => new()
    {
        Name = name ?? $"app-{Guid.NewGuid():N}", OwnerId = ownerId
    };

    private static string ApplicationsRoute(Guid scopeId) => $"/api/scopes/{scopeId}/applications";

    // A System Admin may name any owner (FR-AP-03), but the owner still has to be a real,
    // non-deleted ScopeAdmin who owns the target scope (AF-16b) — CreateApplicationCommandHandler
    // rejects any other OwnerId with OwnerNotValidForScope. The audit-log assertions below are about
    // who *acted* (the System Admin from the bearer token), not who ends up as the application owner,
    // so this seeds a valid owner separately from the actor.

    [FunctionalFact]
    public async Task GivenAuthenticatedSystemAdmin_WhenCreatingApplication_ThenAuditLogRowCarriesActor()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db);
        var admin = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { EmailPrefix = "root" });
        var owner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "owner", Owns = [scope] });
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.SystemAdmin));
        var command = Command(owner.PublicId);

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            ApplicationsRoute(scope.PublicId), command);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Then
        await using var context = db.CreateContext();
        var entry = await context.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == nameof(CreateApplicationCommand)
                               && a.TargetId == response.Body!.Data!.Id);
        Assert.Equal(admin.PublicId, entry.ActorPersonId);
        Assert.Equal((int)Roles.SystemAdmin, entry.ActorRole);
    }

    [FunctionalFact]
    public async Task GivenRejectedCommand_WhenPostingWithNoScope_ThenTheRefusalIsRecorded()
    {
        // Given a scope id nobody holds — the write is rejected before anything is created
        var admin = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { EmailPrefix = "root" });
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            ApplicationsRoute(Guid.NewGuid()), Command(admin.PublicId));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Then — the attempt is recorded, marked as a refusal and carrying why, with the actor it
        // came from. Nothing was created, so there is no target to name.
        await using var context = db.CreateContext();

        var entry = await context.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == nameof(CreateApplicationCommand) && a.ActorPersonId == admin.PublicId);

        Assert.False(entry.Succeeded);
        Assert.Equal(ApplicationMessages.ScopeNotFound, entry.FailureReason);
        Assert.Null(entry.TargetId);
    }

    [FunctionalFact]
    public async Task GivenASuccessfulWrite_WhenAudited_ThenItIsRecordedAsSucceededWithNoReason()
    {
        // The other side of the same row: an allowed write is marked as such, and carries no reason.
        var scope = await Seeds.ScopeAsync(db);
        var admin = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { EmailPrefix = "root" });
        var owner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "owner", Owns = [scope] });
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            ApplicationsRoute(scope.PublicId), Command(owner.PublicId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Then
        await using var context = db.CreateContext();

        var entry = await context.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.TargetId == response.Body!.Data!.Id);

        Assert.True(entry.Succeeded);
        Assert.Null(entry.FailureReason);
    }
}
