using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for DELETE /api/scopes/{id}/pending-registrations/{registrationId} (UC-50,
// FR-SR-12): the main flow for a System Admin and an owning Scope Admin, the discarded link no longer
// confirming (it answers as any unknown token), the discard audited with the registration as target,
// AF-50a (404), AF-50b (403), AF-50c (another scope's registration → 404, row kept), and the framework's
// 403 for a User and 401 anonymous.
[Collection(nameof(FunctionalCollection))]
public class ScopeControllerDiscardPendingRegistrationTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid scopeId, Guid registrationId) =>
        $"/api/scopes/{scopeId}/pending-registrations/{registrationId}";

    private async Task<Scope> SeedScopeAsync(bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var scope = new Scope
        {
            PublicId = Guid.NewGuid(),
            Name = $"scope-{Guid.NewGuid():N}",
            IsDeleted = isDeleted,
            SelfRegistrationEnabled = true
        };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedPersonAsync(Roles role, Scope? ownedScope = null)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = $"{role}",
            Email = $"discard-{Guid.NewGuid():N}@test.local",
            RoleId = (long)role,
            EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        if (ownedScope is not null)
        {
            context.ScopeOwners.Add(new ScopeOwner { ScopeId = ownedScope.Id, PersonId = person.Id });
            await context.SaveChangesAsync();
        }

        return person;
    }

    /// <summary>Writes a pending registration directly, returning it and the plaintext token mailed for it.</summary>
    private async Task<(PendingRegistration Row, string Token)> SeedPendingAsync(Scope scope)
    {
        var token = SingleUseToken.New();

        await using var context = db.CreateContext();
        var row = new PendingRegistration
        {
            ScopeId = scope.Id,
            Name = "Registrant",
            Email = $"registrant-{Guid.NewGuid():N}@test.local",
            PasswordHash = [1],
            Salt = [2],
            TokenHash = SingleUseTokenHash.Of(token),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };
        context.PendingRegistrations.Add(row);
        await context.SaveChangesAsync();

        return (row, token);
    }

    private async Task<bool> StillPendingAsync(PendingRegistration row)
    {
        await using var context = db.CreateContext();
        return await context.PendingRegistrations.AnyAsync(x => x.Id == row.Id);
    }

    private Task<HttpOutput<DataOutput<DiscardPendingRegistrationCommandOutput?>?>> DiscardAsync(
        Guid scopeId, Guid registrationId) =>
        Gateway.DeleteAsync<DataOutput<DiscardPendingRegistrationCommandOutput?>>(Route(scopeId, registrationId));

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenDiscarding_ThenTheRowIsRemovedAndItsLinkNoLongerConfirms()
    {
        // Given a registration whose link the registrant still holds
        var scope = await SeedScopeAsync();
        var (row, token) = await SeedPendingAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await DiscardAsync(scope.PublicId, row.PublicId);

        // Then — removed, and following the link behaves as for any unknown token
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(ScopeMessages.PendingRegistrationDiscarded, response.Body!.Messages);
        Assert.Equal(row.PublicId, response.Body.Data!.Id);
        Assert.False(await StillPendingAsync(row));

        var confirm = await Gateway.PostAsync<DataOutput<VerifyEmailCommandOutput?>>(
            "/api/auth/verify-email", new VerifyEmailCommand { Token = token, Password = "Any-Password-1!" });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Contains(AuthMessages.TokenInvalid, confirm.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenDiscarding_ThenTheDiscardIsAuditedWithTheRegistrationAsTarget()
    {
        // Given an owner of the scope
        var scope = await SeedScopeAsync();
        var owner = await SeedPersonAsync(Roles.ScopeAdmin, ownedScope: scope);
        var (row, _) = await SeedPendingAsync(scope);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await DiscardAsync(scope.PublicId, row.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var context = db.CreateContext();
        var entry = await context.AuditLogs.AsNoTracking().SingleAsync(a =>
            a.Action == nameof(DiscardPendingRegistrationCommand) && a.TargetId == row.PublicId);
        Assert.Equal(owner.PublicId, entry.ActorPersonId);
        Assert.True(entry.Succeeded);
    }

    [FunctionalFact]
    public async Task GivenARegistrationOfAnotherScope_WhenDiscarding_ThenReturnsNotFoundAndKeepsIt()
    {
        // Given AF-50c — the caller owns one scope, the registration is another's
        var scope = await SeedScopeAsync();
        var owner = await SeedPersonAsync(Roles.ScopeAdmin, ownedScope: scope);
        var (row, _) = await SeedPendingAsync(await SeedScopeAsync());
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await DiscardAsync(scope.PublicId, row.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(ScopeMessages.PendingRegistrationNotFound, response.Body!.Errors);
        Assert.True(await StillPendingAsync(row));
    }

    [FunctionalFact]
    public async Task GivenAScopeAdminWhoDoesNotOwnTheScope_WhenDiscarding_ThenReturnsForbiddenAndKeepsIt()
    {
        // Given AF-50b
        var scope = await SeedScopeAsync();
        var stranger = await SeedPersonAsync(Roles.ScopeAdmin, ownedScope: await SeedScopeAsync());
        var (row, _) = await SeedPendingAsync(scope);
        Authorize(TestTokens.For(stranger.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await DiscardAsync(scope.PublicId, row.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(ScopeMessages.NotScopeOwner, response.Body!.Errors);
        Assert.True(await StillPendingAsync(row));
    }

    [FunctionalFact]
    public async Task GivenAnUnknownScope_WhenDiscarding_ThenReturnsNotFound()
    {
        // Given AF-50a
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await DiscardAsync(Guid.NewGuid(), Guid.NewGuid());

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(ScopeMessages.ScopeNotFound, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAnUnknownRegistration_WhenDiscarding_ThenReturnsNotFound()
    {
        // Given AF-50c
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await DiscardAsync(scope.PublicId, Guid.NewGuid());

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(ScopeMessages.PendingRegistrationNotFound, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAUser_WhenDiscarding_ThenReturnsForbidden()
    {
        // Given the role gate
        var scope = await SeedScopeAsync();
        var (row, _) = await SeedPendingAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await DiscardAsync(scope.PublicId, row.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await StillPendingAsync(row));
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenDiscarding_ThenReturnsUnauthorized()
    {
        // Given no bearer token
        var scope = await SeedScopeAsync();
        var (row, _) = await SeedPendingAsync(scope);

        // When
        var response = await DiscardAsync(scope.PublicId, row.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(await StillPendingAsync(row));
    }
}
