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

// Functional tests for POST /api/persons/{id}/verification-email (UC-51, FR-EV-06/07): the main flow
// for a System Admin and an owning Scope Admin, a live token issued for the target and none returned,
// the send audited with the admin as actor and the person as target, AF-51a (unknown id, a Google
// User's id), AF-51b (non-owning Scope Admin, a Scope Admin aiming at an administrator), AF-51c
// (deleted, restricted), AF-51d (verified), AF-51e (a second request inside the cooldown), and the
// framework's 403 for a User and 401 anonymous.
//
// The logging sender writes only a pseudonymous reference to the address (NFR-22), so "the email went
// to the person" is asserted through the token row, which only the target's own send creates.
[Collection(nameof(FunctionalCollection))]
public class PersonControllerSendVerificationEmailTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid personId) => $"/api/persons/{personId}/verification-email";

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedPersonAsync(
        Roles role,
        Scope? memberOf = null,
        Scope? ownerOf = null,
        bool emailVerified = false,
        bool isDeleted = false,
        bool restricted = false)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = $"{role}",
            Email = $"email-{Guid.NewGuid():N}@test.local",
            RoleId = (long)role,
            EmailVerified = emailVerified,
            IsDeleted = isDeleted,
            ProcessingRestrictedAt = restricted ? DateTime.UtcNow : null,
            RestrictionGround = restricted ? (int)RestrictionGrounds.AccuracyContested : null
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        if (memberOf is not null)
        {
            context.ScopeUsers.Add(new ScopeUser { ScopeId = memberOf.Id, PersonId = person.Id });
            person.ScopeId = memberOf.Id;
            await context.SaveChangesAsync();
        }

        if (ownerOf is not null)
        {
            context.ScopeOwners.Add(new ScopeOwner { ScopeId = ownerOf.Id, PersonId = person.Id });
            await context.SaveChangesAsync();
        }

        return person;
    }

    private async Task<int> LiveTokenCountAsync(Person person)
    {
        await using var context = db.CreateContext();
        var now = DateTime.UtcNow;
        return await context.EmailVerificationTokens
            .CountAsync(token => token.PersonId == person.Id && !token.Used && token.ExpiresAt > now);
    }

    private Task<HttpOutput<DataOutput<SendPersonVerificationEmailCommandOutput?>?>> SendAsync(Guid personId) =>
        Gateway.PostAsync<DataOutput<SendPersonVerificationEmailCommandOutput?>>(Route(personId), new { });

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenSending_ThenALiveTokenIsIssuedForThePerson()
    {
        // Given an unverified User
        var target = await SeedPersonAsync(Roles.User, memberOf: await SeedScopeAsync());
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await SendAsync(target.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(PersonMessages.VerificationEmailSentToPerson, response.Body!.Messages);
        Assert.Equal(target.PublicId, response.Body.Data!.Id);
        Assert.Equal(1, await LiveTokenCountAsync(target));
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenSending_ThenTheResponseCarriesNoToken()
    {
        // Given
        var target = await SeedPersonAsync(Roles.ScopeAdmin);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When — read raw, so a field the typed body does not declare would still show
        var raw = await Gateway.Client.PostAsync(Route(target.PublicId), JsonContent.Create(new { }));
        var json = await raw.Content.ReadAsStringAsync();

        // Then
        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [FunctionalFact]
    public async Task GivenASuccessfulSend_WhenAudited_ThenTheEntryNamesTheAdminAndThePerson()
    {
        // Given a seeded System Admin, so the actor can be asserted
        var admin = await SeedPersonAsync(Roles.SystemAdmin, emailVerified: true);
        var target = await SeedPersonAsync(Roles.User, memberOf: await SeedScopeAsync());
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await SendAsync(target.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var context = db.CreateContext();
        var entry = await context.AuditLogs.AsNoTracking().SingleAsync(a =>
            a.Action == nameof(SendPersonVerificationEmailCommand) && a.TargetId == target.PublicId);
        Assert.Equal(admin.PublicId, entry.ActorPersonId);
        Assert.True(entry.Succeeded);
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenSendingToAUserOfTheirScope_ThenItIsSent()
    {
        // Given an owner and a User of the same scope
        var scope = await SeedScopeAsync();
        var owner = await SeedPersonAsync(Roles.ScopeAdmin, ownerOf: scope, emailVerified: true);
        var target = await SeedPersonAsync(Roles.User, memberOf: scope);
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
        // Given AF-51b
        var owner = await SeedPersonAsync(Roles.ScopeAdmin, ownerOf: await SeedScopeAsync(), emailVerified: true);
        var target = await SeedPersonAsync(Roles.User, memberOf: await SeedScopeAsync());
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await SendAsync(target.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(PersonMessages.NotAuthorizedToEmailPerson, response.Body!.Errors);
        Assert.Equal(0, await LiveTokenCountAsync(target));
    }

    [FunctionalFact]
    public async Task GivenScopeAdmin_WhenSendingToAnotherScopeAdmin_ThenReturnsForbidden()
    {
        // Given AF-51b — a co-owner is not a User
        var scope = await SeedScopeAsync();
        var owner = await SeedPersonAsync(Roles.ScopeAdmin, ownerOf: scope, emailVerified: true);
        var coOwner = await SeedPersonAsync(Roles.ScopeAdmin, ownerOf: scope);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await SendAsync(coOwner.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenAnUnknownId_WhenSending_ThenReturnsNotFound()
    {
        // Given AF-51a
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await SendAsync(Guid.NewGuid());

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(PersonMessages.PersonNotFound, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAGoogleUsersId_WhenSending_ThenReturnsNotFound()
    {
        // Given AF-51a — Google Users are not persons and have no verification email
        var scope = await SeedScopeAsync();
        await using var context = db.CreateContext();
        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = $"google-sub-{Guid.NewGuid():N}",
            Name = "Google Signer",
            Email = $"signer-{Guid.NewGuid():N}@gmail.test",
            EmailVerified = true,
            ScopeId = scope.Id
        };
        context.GoogleUsers.Add(googleUser);
        await context.SaveChangesAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await SendAsync(googleUser.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenALogicallyDeletedPerson_WhenSending_ThenReturnsConflict()
    {
        // Given AF-51c
        var target = await SeedPersonAsync(Roles.User, memberOf: await SeedScopeAsync(), isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await SendAsync(target.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(PersonMessages.PersonIsDeleted, response.Body!.Errors);
        Assert.Equal(0, await LiveTokenCountAsync(target));
    }

    [FunctionalFact]
    public async Task GivenARestrictedPerson_WhenSending_ThenReturnsConflict()
    {
        // Given AF-51c (NFR-24)
        var target = await SeedPersonAsync(Roles.User, memberOf: await SeedScopeAsync(), restricted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await SendAsync(target.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(PersonMessages.PersonProcessingRestricted, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAVerifiedAddress_WhenSending_ThenReturnsBadRequest()
    {
        // Given AF-51d
        var target = await SeedPersonAsync(Roles.User, memberOf: await SeedScopeAsync(), emailVerified: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await SendAsync(target.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.EmailAlreadyVerified, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenASecondRequestInsideTheCooldown_WhenSending_ThenReturnsTooManyRequestsAndSendsNothing()
    {
        // Given AF-51e — a first send has just succeeded
        var admin = await SeedPersonAsync(Roles.SystemAdmin, emailVerified: true);
        var target = await SeedPersonAsync(Roles.User, memberOf: await SeedScopeAsync());
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.SystemAdmin));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(target.PublicId)).StatusCode);

        // When
        var response = await SendAsync(target.PublicId);

        // Then — still exactly the first link, and the refusal is in the trail
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains(PersonMessages.AccountEmailSentRecently, response.Body!.Errors);
        Assert.Equal(1, await LiveTokenCountAsync(target));

        await using var context = db.CreateContext();
        var refusal = await context.AuditLogs.AsNoTracking().SingleAsync(a =>
            a.Action == nameof(SendPersonVerificationEmailCommand) && a.ActorPersonId == admin.PublicId && !a.Succeeded);
        Assert.Equal(PersonMessages.AccountEmailSentRecently, refusal.FailureReason);
    }

    [FunctionalFact]
    public async Task GivenAUser_WhenSending_ThenReturnsForbidden()
    {
        // Given the role gate
        var target = await SeedPersonAsync(Roles.User, memberOf: await SeedScopeAsync());
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
