using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
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

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for GET /api/audit-log (UC-48, FR-AT-01/02): an entry written by an earlier request
// in the same test is listed with its actor, action and target; a refusal is listed with its reason;
// a cleared actor reads as null; offset-less bounds and a blank action filter are accepted; AF-48a
// (400), AF-48b (403 for a Scope Admin and a User, 401 anonymous), AF-48c (an empty page); and the
// read itself writes no audit entry.
//
// The trail is shared by the whole suite, so every listing filters by something this test alone owns
// — a seeded actor or a fresh target id.
[Collection(nameof(FunctionalCollection))]
public class AuditControllerListTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Route = "/api/audit-log";

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedPersonAsync(Roles role)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = $"{role}",
            Email = $"audit-{Guid.NewGuid():N}@test.local",
            RoleId = (long)role,
            EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    /// <summary>Writes an entry directly — the only way to have one whose actor is already cleared.</summary>
    private async Task<AuditLog> SeedEntryAsync(Guid targetId, DateTime createdAt, Guid? actor = null)
    {
        await using var context = db.CreateContext();
        var entry = new AuditLog
        {
            PublicId = Guid.NewGuid(),
            ActorPersonId = actor,
            ActorRole = actor is null ? null : (int)Roles.SystemAdmin,
            Action = "FunctionalSeedCommand",
            TargetId = targetId,
            Succeeded = true,
            CreatedAt = createdAt
        };
        context.AuditLogs.Add(entry);
        await context.SaveChangesAsync();
        return entry;
    }

    /// <summary>
    ///     Switches the gateway to another caller. <c>Authorize</c> adds the header rather than replacing
    ///     it, so a test acting as two people in turn clears the first one.
    /// </summary>
    private void Reauthorize(string authToken)
    {
        Gateway.Client.DefaultRequestHeaders.Remove("Authorization");
        Authorize(authToken);
    }

    private Task<HttpOutput<PaginatedOutput<AuditLogEntryOutput>?>> ListAsync(string filters) =>
        Gateway.GetAsync<PaginatedOutput<AuditLogEntryOutput>>($"{Route}?pageNumber=1&pageSize=20&{filters}");

    [FunctionalFact]
    public async Task GivenAnEarlierWriteInTheSameTest_WhenListingByTarget_ThenTheEntryNamesActorActionAndTarget()
    {
        // Given a System Admin who has just turned Google Sign-In on for a scope
        var admin = await SeedPersonAsync(Roles.SystemAdmin);
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.SystemAdmin));
        var write = await Gateway.PutAsync<DataOutput<SetGoogleSignInCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/google-signin", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);

        // When
        var response = await ListAsync($"action={nameof(SetGoogleSignInCommand)}&targetId={scope.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(AuditMessages.AuditLogRetrieved, response.Body!.Messages);
        var entry = Assert.Single(response.Body.Data!);
        Assert.Equal(admin.PublicId, entry.ActorId);
        Assert.Equal((int)Roles.SystemAdmin, entry.ActorRole);
        Assert.Equal(nameof(SetGoogleSignInCommand), entry.Action);
        Assert.Equal(scope.PublicId, entry.TargetId);
        Assert.True(entry.Succeeded);
        Assert.Null(entry.FailureReason);
    }

    [FunctionalFact]
    public async Task GivenARefusedWrite_WhenListingRefusalsByActor_ThenItCarriesTheReason()
    {
        // Given a Scope Admin refused a scope they do not own
        var scopeAdmin = await SeedPersonAsync(Roles.ScopeAdmin);
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.For(scopeAdmin.PublicId, (int)Roles.ScopeAdmin));
        var refused = await Gateway.PutAsync<DataOutput<SetGoogleSignInCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/google-signin", new { enabled = true });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Reauthorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await ListAsync($"actorId={scopeAdmin.PublicId}&succeeded=false");

        // Then — a refusal has no output, so it names no target
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = Assert.Single(response.Body!.Data!);
        Assert.Equal(nameof(SetGoogleSignInCommand), entry.Action);
        Assert.Equal((int)Roles.ScopeAdmin, entry.ActorRole);
        Assert.False(entry.Succeeded);
        Assert.Equal(ScopeMessages.NotScopeOwner, entry.FailureReason);
        Assert.Null(entry.TargetId);
    }

    [FunctionalFact]
    public async Task GivenAnEntryWhoseActorWasCleared_WhenListed_ThenActorFieldsAreNull()
    {
        // Given an entry as the retention pass leaves it
        var target = Guid.NewGuid();
        var seeded = await SeedEntryAsync(target, DateTime.UtcNow, actor: null);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await ListAsync($"targetId={target}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = Assert.Single(response.Body!.Data!);
        Assert.Equal(seeded.PublicId, entry.Id);
        Assert.Null(entry.ActorId);
        Assert.Null(entry.ActorRole);
    }

    [FunctionalFact]
    public async Task GivenBoundsWithoutAnOffset_WhenListing_ThenTheyAreReadAsUtc()
    {
        // Given an entry recorded at 10:00 UTC, and bounds as a datetime-local input sends them
        var target = Guid.NewGuid();
        await SeedEntryAsync(target, new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc));
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var inside = await ListAsync($"targetId={target}&from=2026-03-01T09:00:00&to=2026-03-01T11:00:00");
        var outside = await ListAsync($"targetId={target}&from=2026-03-01T10:30:00");

        // Then — 200 both times, not a 500 from an unspecified DateTime
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);
        Assert.Single(inside.Body!.Data!);
        Assert.Equal(HttpStatusCode.OK, outside.StatusCode);
        Assert.Empty(outside.Body!.Data!);
    }

    [FunctionalFact]
    public async Task GivenABlankActionFilter_WhenListing_ThenItIsIgnored()
    {
        // Given one entry about a fresh target
        var target = Guid.NewGuid();
        await SeedEntryAsync(target, DateTime.UtcNow);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When — an empty value, and a whitespace one
        var empty = await ListAsync($"targetId={target}&action=");
        var spaces = await ListAsync($"targetId={target}&action=%20%20");

        // Then
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Single(empty.Body!.Data!);
        Assert.Equal(HttpStatusCode.OK, spaces.StatusCode);
        Assert.Single(spaces.Body!.Data!);
    }

    [FunctionalFact]
    public async Task GivenToEarlierThanFrom_WhenListing_ThenReturnsBadRequest()
    {
        // Given AF-48a
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await ListAsync("from=2026-03-02T00:00:00Z&to=2026-03-01T00:00:00Z");

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuditMessages.InvalidDateRange, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenNothingMatches_WhenListing_ThenReturnsAnEmptyPage()
    {
        // Given AF-48c — a target nothing was ever done to
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await ListAsync($"targetId={Guid.NewGuid()}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, response.Body!.TotalItems);
        Assert.Empty(response.Body.Data!);
    }

    [FunctionalFact]
    public async Task GivenAListing_WhenRead_ThenNoAuditEntryIsWritten()
    {
        // Given a System Admin who has done nothing yet
        var admin = await SeedPersonAsync(Roles.SystemAdmin);
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await ListAsync($"targetId={Guid.NewGuid()}");

        // Then — reads are not audited
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var context = db.CreateContext();
        Assert.False(await context.AuditLogs.AnyAsync(entry => entry.ActorPersonId == admin.PublicId));
    }

    [FunctionalFact]
    public async Task GivenAScopeAdmin_WhenListing_ThenReturnsForbidden()
    {
        // Given AF-48b — the trail is System Admins' only
        Authorize(TestTokens.ForRole((int)Roles.ScopeAdmin));

        // When
        var response = await ListAsync($"targetId={Guid.NewGuid()}");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenAUser_WhenListing_ThenReturnsForbidden()
    {
        // Given AF-48b
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await ListAsync($"targetId={Guid.NewGuid()}");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenListing_ThenReturnsUnauthorized()
    {
        // Given no bearer token
        // When
        var response = await ListAsync($"targetId={Guid.NewGuid()}");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
