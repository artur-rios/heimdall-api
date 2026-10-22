using System.Net;
using System.Net.Http.Json;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
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

// Functional tests for POST /api/persons/{id}/password-reset (UC-52, FR-PR-05/06): the main flow for a
// System Admin and an owning Scope Admin, a live reset token issued for the target and none returned,
// the send audited with the admin as actor and the person as target, AF-52a (404), AF-52b (403), AF-52c
// (deleted, restricted → 409), AF-52d (a User whose scope is deleted → 409), AF-52e (429), and the
// framework's 403 for a User and 401 anonymous.
[Collection(nameof(FunctionalCollection))]
public class PersonControllerSendPasswordResetTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid personId) => $"/api/persons/{personId}/password-reset";

    private async Task<int> LiveTokenCountAsync(Person person)
    {
        await using var context = db.CreateContext();
        var now = DateTime.UtcNow;
        return await context.PasswordResetTokens
            .CountAsync(token => token.PersonId == person.Id && !token.Used && token.ExpiresAt > now);
    }

    private Task<HttpOutput<DataOutput<SendPersonPasswordResetCommandOutput?>?>> SendAsync(Guid personId) =>
        Gateway.PostAsync<DataOutput<SendPersonPasswordResetCommandOutput?>>(Route(personId), new { });

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenSending_ThenALiveResetTokenIsIssuedForThePerson()
    {
        // Given a User of a live scope
        var target = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "reset", MemberOf = await Seeds.ScopeAsync(db) });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await SendAsync(target.PublicId);

        // Then — answered after the send, not queued as UC-12 is
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(PersonMessages.PasswordResetSentToPerson, response.Body!.Messages);
        Assert.Equal(target.PublicId, response.Body.Data!.Id);
        Assert.Equal(1, await LiveTokenCountAsync(target));
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenSending_ThenTheResponseCarriesNoToken()
    {
        // Given a System Admin as target — any person may be reset by a System Admin
        var target = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { EmailPrefix = "reset" });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var raw = await Gateway.Client.PostAsync(Route(target.PublicId), JsonContent.Create(new { }));
        var json = await raw.Content.ReadAsStringAsync();

        // Then
        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [FunctionalFact]
    public async Task GivenASuccessfulSend_WhenAudited_ThenTheEntryNamesTheAdminAndThePerson()
    {
        // Given a seeded System Admin
        var admin = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { EmailPrefix = "reset" });
        var target = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "reset", MemberOf = await Seeds.ScopeAsync(db) });
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await SendAsync(target.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var context = db.CreateContext();
        var entry = await context.AuditLogs.AsNoTracking().SingleAsync(a =>
            a.Action == nameof(SendPersonPasswordResetCommand) && a.TargetId == target.PublicId);
        Assert.Equal(admin.PublicId, entry.ActorPersonId);
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenSendingToAUserOfTheirScope_ThenItIsSent()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db);
        var owner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "reset", Owns = [scope] });
        var target = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "reset", MemberOf = scope });
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await SendAsync(target.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await LiveTokenCountAsync(target));
    }

    [FunctionalFact]
    public async Task GivenScopeAdmin_WhenSendingToAUserOfAnotherScope_ThenReturnsForbidden()
    {
        // Given AF-52b
        var owner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "reset", Owns = [await Seeds.ScopeAsync(db)] });
        var target = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "reset", MemberOf = await Seeds.ScopeAsync(db) });
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await SendAsync(target.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(PersonMessages.NotAuthorizedToEmailPerson, response.Body!.Errors);
        Assert.Equal(0, await LiveTokenCountAsync(target));
    }

    [FunctionalFact]
    public async Task GivenAnUnknownId_WhenSending_ThenReturnsNotFound()
    {
        // Given AF-52a
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await SendAsync(Guid.NewGuid());

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(PersonMessages.PersonNotFound, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenALogicallyDeletedPerson_WhenSending_ThenReturnsConflict()
    {
        // Given AF-52c
        var target = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "reset", IsDeleted = true, MemberOf = await Seeds.ScopeAsync(db) });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await SendAsync(target.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(PersonMessages.PersonIsDeleted, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenARestrictedPerson_WhenSending_ThenReturnsConflict()
    {
        // Given AF-52c (NFR-24)
        var target = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "reset", RestrictedAt = DateTime.UtcNow, MemberOf = await Seeds.ScopeAsync(db) });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await SendAsync(target.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(PersonMessages.PersonProcessingRestricted, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAUserWhoseScopeIsDeleted_WhenSending_ThenReturnsConflict()
    {
        // Given AF-52d — a live User of a logically deleted scope could not sign in
        var target = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "reset", MemberOf = await Seeds.ScopeAsync(db, new ScopeOptions { IsDeleted = true }) });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await SendAsync(target.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(PersonMessages.PersonCannotSignIn, response.Body!.Errors);
        Assert.Equal(0, await LiveTokenCountAsync(target));
    }

    [FunctionalFact]
    public async Task GivenASecondRequestInsideTheCooldown_WhenSending_ThenReturnsTooManyRequests()
    {
        // Given AF-52e — a first send has just succeeded
        var target = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "reset", MemberOf = await Seeds.ScopeAsync(db) });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(target.PublicId)).StatusCode);

        // When
        var response = await SendAsync(target.PublicId);

        // Then — nothing more sent
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains(PersonMessages.AccountEmailSentRecently, response.Body!.Errors);
        Assert.Equal(1, await LiveTokenCountAsync(target));
    }

    [FunctionalFact]
    public async Task GivenAUser_WhenSending_ThenReturnsForbidden()
    {
        // Given the role gate
        var target = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "reset", MemberOf = await Seeds.ScopeAsync(db) });
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await SendAsync(target.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenSending_ThenReturnsUnauthorized()
    {
        // Given no bearer token
        // When
        var response = await SendAsync(Guid.NewGuid());

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
