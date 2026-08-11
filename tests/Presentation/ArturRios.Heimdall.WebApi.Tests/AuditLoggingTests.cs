using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for NFR-09 (audit logging): proves a successful write produces one AuditLog row
// carrying the acting caller's identity, and that a rejected write (one that never mutates anything)
// produces no row at all.
[Collection(nameof(FunctionalCollection))]
public class AuditLoggingTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static CreateApplicationCommand Command(Guid ownerId, string? name = null) => new()
    {
        Name = name ?? $"app-{Guid.NewGuid():N}", OwnerId = ownerId
    };

    private static string ApplicationsRoute(Guid scopeId) => $"/api/scopes/{scopeId}/applications";

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedSystemAdminAsync()
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Root",
            Email = $"root-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.SystemAdmin, EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    // A System Admin may name any owner (FR-AP-03), but the owner still has to be a real,
    // non-deleted ScopeAdmin who owns the target scope (AF-16b) — CreateApplicationCommandHandler
    // rejects any other OwnerId with OwnerNotValidForScope. The audit-log assertions below are about
    // who *acted* (the System Admin from the bearer token), not who ends up as the application owner,
    // so this seeds a valid owner separately from the actor.
    private async Task<Person> SeedScopeOwnerAsync(Scope scope)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Owner",
            Email = $"owner-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.ScopeAdmin, EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.ScopeOwners.Add(new ScopeOwner { ScopeId = scope.Id, PersonId = person.Id });
        await context.SaveChangesAsync();

        return person;
    }

    [FunctionalFact]
    public async Task GivenAuthenticatedSystemAdmin_WhenCreatingApplication_ThenAuditLogRowCarriesActor()
    {
        // Given
        var scope = await SeedScopeAsync();
        var admin = await SeedSystemAdminAsync();
        var owner = await SeedScopeOwnerAsync(scope);
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
    public async Task GivenRejectedCommand_WhenPostingWithNoScope_ThenNoAuditLogRowIsWritten()
    {
        // Given a scope id nobody holds — the write is rejected before anything is created
        var admin = await SeedSystemAdminAsync();
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            ApplicationsRoute(Guid.NewGuid()), Command(admin.PublicId));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Then
        await using var context = db.CreateContext();
        Assert.False(await context.AuditLogs.AnyAsync(a => a.Action == nameof(CreateApplicationCommand)
                                                             && a.ActorPersonId == admin.PublicId));
    }
}
