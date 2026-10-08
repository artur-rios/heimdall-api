using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for the one exception ActorLivenessFilter makes (NFR-24, UC-41, UC-42, UC-44,
// UC-45): a restricted or suspended subject still reaches the endpoints through which they exercise
// their own rights, and nothing else.
//
// Before, the filter refused a restricted or logically deleted identity everywhere, so NFR-24's
// "remaining reachable by its own subject's export" and UC-45's "the subject lifts their own" could not
// happen, and UC-41's and UC-44's notes that a suspended identity may still export and restrict were
// dead letters. Each use case already defines what a non-live caller gets (AF-41a, AF-42b, AF-44a,
// AF-44c, AF-45a/c), so on these endpoints the filter leaves that decision to the handler.
[Collection(nameof(FunctionalCollection))]
public class SubjectRightsLivenessTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-Rights-Pass!";

    private async Task<Person> SeedPersonAsync(
        Roles role = Roles.User, bool restricted = false, bool isDeleted = false, bool anonymised = false)
    {
        await using var context = db.CreateContext();

        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Rights Holder",
            Email = $"rights-{Guid.NewGuid():N}@functional.test",
            PasswordHash = Hash.EncodeWithRandomSalt(Password, out var salt),
            Salt = salt,
            RoleId = (long)role,
            EmailVerified = true,
            ProcessingRestrictedAt = restricted ? DateTime.UtcNow.AddDays(-1) : null,
            RestrictionGround = restricted ? (int)RestrictionGrounds.AccuracyContested : null,
            IsDeleted = isDeleted || anonymised,
            DeletedAt = isDeleted || anonymised ? DateTime.UtcNow.AddDays(-1) : null,
            AnonymisedAt = anonymised ? DateTime.UtcNow : null
        };

        context.Persons.Add(person);
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<Person> ReloadAsync(Person person)
    {
        await using var context = db.CreateContext();
        return await context.Persons.SingleAsync(x => x.Id == person.Id);
    }

    private Task<HttpOutput<DataOutput<DataExportCommandOutput?>?>> ExportAsync() =>
        Gateway.PostAsync<DataOutput<DataExportCommandOutput?>>("/api/auth/data-export", new { });

    // A restricted subject: the four rights endpoints are reachable

    [FunctionalFact]
    public async Task GivenARestrictedPerson_WhenExportingTheirData_ThenTheCopyIsReturned()
    {
        // Given — NFR-24: "remaining reachable by its own subject's export"
        var person = await SeedPersonAsync(restricted: true);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        // When
        var response = await ExportAsync();

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Body?.Data);
    }

    [FunctionalFact]
    public async Task GivenARestrictedPerson_WhenLiftingTheirOwn_ThenItIsLiftedWithoutNotification()
    {
        // Given — UC-45: the subject lifts their own, and needs no Art. 18(3) notice to do it
        var person = await SeedPersonAsync(restricted: true);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.PostAsync<DataOutput<LiftProcessingRestrictionCommandOutput?>>(
            "/api/auth/processing-restriction/lift", new { });

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Body?.Data?.SubjectNotified);

        var stored = await ReloadAsync(person);

        Assert.Null(stored.ProcessingRestrictedAt);
        Assert.Null(stored.RestrictionLiftNotifiedAt);
    }

    [FunctionalFact]
    public async Task GivenARestrictedPerson_WhenRestrictingAgain_ThenTheUseCasesOwnConflict()
    {
        // Given — AF-44a is reachable only if the filter lets a restricted caller through
        var person = await SeedPersonAsync(restricted: true);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.PostAsync<DataOutput<RestrictProcessingCommandOutput?>>(
            "/api/auth/processing-restriction", new { ground = (int)RestrictionGrounds.ObjectionPending });

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(ErasureMessages.ProcessingAlreadyRestricted, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenARestrictedPerson_WhenRequestingErasure_ThenTheRequestIsRecorded()
    {
        // Given — UC-42 is self-service whatever the account's standing (DPIA R-06); the request is
        // the subject's own, so Art. 18(2)'s consent condition is met by asking
        var person = await SeedPersonAsync(restricted: true);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.PostAsync<DataOutput<RequestErasureCommandOutput?>>(
            "/api/auth/erasure-request", new { password = Password });

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await ReloadAsync(person);

        Assert.NotNull(stored.ErasureRequestedAt);

        // The restriction still stands: the record waits, unanonymised, until it is lifted (NFR-24)
        Assert.NotNull(stored.ProcessingRestrictedAt);
    }

    // A restricted subject: everything else is still refused

    [FunctionalFact]
    public async Task GivenARestrictedPerson_WhenCallingAnyOtherEndpoint_ThenUnauthorized()
    {
        // Given
        var person = await SeedPersonAsync(restricted: true);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        // When — a read of their own record, a 2FA read and write, and an email send: none of them a
        // right NFR-24 keeps reachable
        var reads = new[]
        {
            await Gateway.GetAsync<DataOutput<object?>>($"/api/persons/{person.PublicId}"),
            await Gateway.GetAsync<DataOutput<object?>>("/api/auth/2fa")
        };
        var writes = new[]
        {
            await Gateway.PostAsync<DataOutput<object?>>("/api/auth/resend-verification", new { }),
            await Gateway.PostAsync<DataOutput<object?>>("/api/auth/2fa/enable", new { appEnabled = true }),
            await Gateway.PutAsync<DataOutput<object?>>($"/api/persons/{person.PublicId}", new { name = "Changed" })
        };

        // Then
        Assert.All(reads.Concat(writes), response =>
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains(ActorLivenessFilter.ActorNotLive, response.Body!.Errors);
        });

        Assert.Equal("Rights Holder", (await ReloadAsync(person)).Name);
    }

    [FunctionalFact]
    public async Task GivenARestrictedSystemAdmin_WhenLiftingSomebodyElses_ThenForbiddenAndItStands()
    {
        // Given — the lift endpoint is reachable for the caller's own restriction only. A restricted
        // System Admin is a subject like any other there, not an administrator.
        var admin = await SeedPersonAsync(Roles.SystemAdmin, restricted: true);
        var subject = await SeedPersonAsync(restricted: true);
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<LiftProcessingRestrictionCommandOutput?>>(
            "/api/auth/processing-restriction/lift", new { subjectId = subject.PublicId });

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(ErasureMessages.NotEligible, response.Body!.Errors);
        Assert.NotNull((await ReloadAsync(subject)).ProcessingRestrictedAt);
    }

    [FunctionalFact]
    public async Task GivenARestrictedGoogleUser_WhenExportingTheirData_ThenTheCopyIsReturned()
    {
        // Given a restricted Google User — the filter's second table
        Guid googleUserId;
        Guid scopeId;

        await using (var context = db.CreateContext())
        {
            var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
            context.Scopes.Add(scope);
            await context.SaveChangesAsync();

            var googleUser = new GoogleUser
            {
                PublicId = Guid.NewGuid(),
                GoogleId = $"google-{Guid.NewGuid():N}",
                Name = "Restricted Google User",
                Email = $"google-{Guid.NewGuid():N}@functional.test",
                ScopeId = scope.Id,
                ProcessingRestrictedAt = DateTime.UtcNow.AddDays(-1),
                RestrictionGround = (int)RestrictionGrounds.AccuracyContested
            };
            context.GoogleUsers.Add(googleUser);
            await context.SaveChangesAsync();

            googleUserId = googleUser.PublicId;
            scopeId = scope.PublicId;
        }

        Authorize(TestTokens.For(googleUserId, (int)Roles.User, scopeId));

        // When
        var export = await ExportAsync();
        var signOut = await Gateway.PostAsync<DataOutput<object?>>("/api/auth/google/sign-out", new { });

        // Then — the export is a right; signing out of a session they may not have is not
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, signOut.StatusCode);
    }

    // A suspended (logically deleted) subject: UC-41 and UC-44 say they may still export and restrict

    [FunctionalFact]
    public async Task GivenASuspendedPerson_WhenExportingAndRestricting_ThenBothAreServed()
    {
        // Given — UC-41: "A suspended identity can still export"; UC-44: "A suspended identity may
        // still restrict"
        var person = await SeedPersonAsync(isDeleted: true);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        // When
        var export = await ExportAsync();
        var restriction = await Gateway.PostAsync<DataOutput<RestrictProcessingCommandOutput?>>(
            "/api/auth/processing-restriction", new { ground = (int)RestrictionGrounds.AccuracyContested });

        // Then
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal(HttpStatusCode.OK, restriction.StatusCode);
        Assert.NotNull((await ReloadAsync(person)).ProcessingRestrictedAt);
    }

    [FunctionalFact]
    public async Task GivenASuspendedPerson_WhenRequestingErasure_ThenTheUseCasesOwnRefusal()
    {
        // Given — AF-42b: a token naming no live identity is NotEligible (403), the use case's answer
        // rather than the filter's generic 401
        var person = await SeedPersonAsync(isDeleted: true);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.PostAsync<DataOutput<RequestErasureCommandOutput?>>(
            "/api/auth/erasure-request", new { password = Password });

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(ErasureMessages.NotEligible, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAnAnonymisedPerson_WhenExporting_ThenUnauthorized()
    {
        // Given an identity already anonymised (NFR-20): there is no subject left to serve
        var person = await SeedPersonAsync(anonymised: true);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        // When
        var response = await ExportAsync();

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(ActorLivenessFilter.ActorNotLive, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenARestrictedPersonWithAStaleRoleClaim_WhenExporting_ThenUnauthorized()
    {
        // Given a restricted User whose token claims System Admin: the exception covers liveness, not
        // TH-08's role check, which still runs on every endpoint
        var person = await SeedPersonAsync(restricted: true);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await ExportAsync();

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(ActorLivenessFilter.ActorRoleChanged, response.Body!.Errors);
    }
}
