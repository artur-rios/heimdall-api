using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for UC-44/UC-45 over the real pipeline. The end-to-end behaviours that matter:
// restriction and deletion stay independent in the database, a restricted identity stops being able
// to act immediately, and only the right callers can lift.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerRestrictProcessingTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = Seeds.Password;

    private async Task<Person> ReloadAsync(Person person)
    {
        await using var context = db.CreateContext();
        return await context.Persons.SingleAsync(x => x.Id == person.Id);
    }

    [FunctionalFact]
    public async Task GivenAPerson_WhenRestrictingProcessing_ThenNothingIsDeleted()
    {
        var person = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Name = "Ada", EmailPrefix = "restrict" });
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        var response = await Gateway.PostAsync<DataOutput<RestrictProcessingCommandOutput?>>(
            "/api/auth/processing-restriction",
            new { ground = (int)RestrictionGrounds.AccuracyContested });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await ReloadAsync(person);

        Assert.NotNull(stored.ProcessingRestrictedAt);

        // The two states are independent: the record is preserved exactly as it stands, which is the
        // opposite of what the deletion flag sets in motion
        Assert.False(stored.IsDeleted);
        Assert.Null(stored.DeletedAt);
        Assert.Equal("Ada", stored.Name);
    }

    [FunctionalFact]
    public async Task GivenAnUnknownGround_WhenRestricting_ThenBadRequest()
    {
        var person = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Name = "Ada", EmailPrefix = "restrict" });
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        var response = await Gateway.PostAsync<DataOutput<RestrictProcessingCommandOutput?>>(
            "/api/auth/processing-restriction", new { ground = 99 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await ReloadAsync(person)).ProcessingRestrictedAt);
    }

    [FunctionalFact]
    public async Task GivenARestrictedPerson_WhenTheyActOnAnExistingToken_ThenTheyAreRefused()
    {
        // The restriction takes effect immediately: ActorLivenessFilter re-reads the record on every
        // request, so a token issued before it does not keep working — outside the subject-rights
        // endpoints SubjectRightsLivenessTests covers, the subject lifting their own included.
        var person = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Name = "Ada", EmailPrefix = "restrict", RestrictedAt = DateTime.UtcNow });
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        var response = await Gateway.GetAsync<DataOutput<object>>($"/api/persons/{person.PublicId}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenARestrictedPerson_WhenTheyLogInLiftAndLogInAgain_ThenOnlyTheSecondTokenIsAFullOne()
    {
        // Given a person restricted long enough ago that every token they held has expired (#145) —
        // a System Admin, who signs in system-wide, where a User would need a scope
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Name = "Ada", EmailPrefix = "restrict", RestrictedAt = DateTime.UtcNow.AddDays(-30) });

        // When they sign in — UC-11 AF-11i: a subject-rights-only token, not a refusal
        var login = await LoginAsync(person.Email, Password);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(login.Body!.Data!.ProcessingRestricted);
        Assert.Contains(AuthMessages.SubjectRightsOnlySignIn, login.Body.Messages);
        var rightsOnly = login.Body.Data.Token!;

        // Then it is refused outside the subject's rights, with 403 rather than the liveness 401
        Authorize(rightsOnly);
        var elsewhere = await Gateway.GetAsync<DataOutput<object>>($"/api/persons/{person.PublicId}");
        Assert.Equal(HttpStatusCode.Forbidden, elsewhere.StatusCode);
        Assert.Contains(SubjectRightsOnlyGuardFilter.SubjectRightsOnly, elsewhere.Body!.Errors);

        // And it lifts their own restriction (UC-45), with no notification since they are the subject
        var lift = await Gateway.PostAsync<DataOutput<LiftProcessingRestrictionCommandOutput?>>(
            "/api/auth/processing-restriction/lift", new { });
        Assert.Equal(HttpStatusCode.OK, lift.StatusCode);
        Assert.Null((await ReloadAsync(person)).ProcessingRestrictedAt);

        // It stays confined once the restriction is gone — it is not a way back into the account
        var afterLift = await Gateway.GetAsync<DataOutput<object>>($"/api/persons/{person.PublicId}");
        Assert.Equal(HttpStatusCode.Forbidden, afterLift.StatusCode);

        // Signing in again issues the full token
        var again = await LoginAsync(person.Email, Password);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.False(again.Body!.Data!.ProcessingRestricted);

        Authorize(again.Body.Data.Token!);
        var full = await Gateway.GetAsync<DataOutput<object>>($"/api/persons/{person.PublicId}");
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenARestrictedPersonWithTheWrongPassword_WhenTheyLogIn_ThenTheUniformRefusal()
    {
        // The restriction is only revealed to somebody holding the credential: a wrong password is
        // answered exactly like any other refusal, so the endpoint does not disclose a dispute
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Name = "Ada", EmailPrefix = "restrict", RestrictedAt = DateTime.UtcNow });

        var response = await LoginAsync(person.Email, "Wr0ng-Restrict-Pass!");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(AuthMessages.InvalidCredentials, response.Body!.Errors);
        Assert.Null(response.Body.Data?.Token);
    }

    [FunctionalFact]
    public async Task GivenASuspendedRestrictedPerson_WhenTheyLogIn_ThenTheyGetASubjectRightsOnlyToken()
    {
        // A suspended (not yet anonymised) subject keeps their rights over the record, so the
        // deletion check AF-11c makes does not apply to a restricted one
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Name = "Ada", EmailPrefix = "restrict", RestrictedAt = DateTime.UtcNow });

        await using (var context = db.CreateContext())
        {
            // A targeted write, as the application makes it: a whole-row save ignores the column (#237).
            await context.Persons.Where(x => x.Id == person.Id)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(x => x.IsDeleted, true)
                    .SetProperty(x => x.DeletedAt, DateTime.UtcNow));
        }

        var login = await LoginAsync(person.Email, Password);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(login.Body!.Data!.ProcessingRestricted);

        Authorize(login.Body.Data.Token!);
        var export = await Gateway.PostAsync<DataOutput<DataExportCommandOutput?>>("/api/auth/data-export", new { });
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenARestrictedSystemAdminsSubjectRightsToken_WhenLiftingSomebodyElses_ThenForbidden()
    {
        // The token acts for its subject alone (AF-45c), even though it carries the admin role
        var admin = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Name = "Ada", EmailPrefix = "restrict", RestrictedAt = DateTime.UtcNow });
        var other = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Name = "Ada", EmailPrefix = "restrict", RestrictedAt = DateTime.UtcNow.AddDays(-1) });

        var login = await LoginAsync(admin.Email, Password);
        Assert.True(login.Body!.Data!.ProcessingRestricted);
        Authorize(login.Body.Data.Token!);

        var response = await Gateway.PostAsync<DataOutput<LiftProcessingRestrictionCommandOutput?>>(
            "/api/auth/processing-restriction/lift", new { subjectId = other.PublicId });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull((await ReloadAsync(other)).ProcessingRestrictedAt);
    }

    private Task<HttpOutput<DataOutput<LoginCommandOutput?>?>> LoginAsync(string email, string password) =>
        Gateway.PostAsync<DataOutput<LoginCommandOutput?>>("/api/auth/login", new { email, password });

    [FunctionalFact]
    public async Task GivenASystemAdmin_WhenLiftingSomebodyElses_ThenTheSubjectIsInformedFirst()
    {
        var person = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Name = "Ada", EmailPrefix = "restrict", RestrictedAt = DateTime.UtcNow.AddDays(-1) });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await Gateway.PostAsync<DataOutput<LiftProcessingRestrictionCommandOutput?>>(
            "/api/auth/processing-restriction/lift", new { subjectId = person.PublicId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body?.Data?.SubjectNotified);

        var stored = await ReloadAsync(person);

        Assert.Null(stored.ProcessingRestrictedAt);
        Assert.NotNull(stored.RestrictionLiftNotifiedAt);
    }

    [FunctionalFact]
    public async Task GivenAScopeAdmin_WhenLiftingSomebodyElses_ThenForbidden()
    {
        var person = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Name = "Ada", EmailPrefix = "restrict", RestrictedAt = DateTime.UtcNow.AddDays(-1) });
        Authorize(TestTokens.ForRole((int)Roles.ScopeAdmin));

        var response = await Gateway.PostAsync<DataOutput<LiftProcessingRestrictionCommandOutput?>>(
            "/api/auth/processing-restriction/lift", new { subjectId = person.PublicId });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull((await ReloadAsync(person)).ProcessingRestrictedAt);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenRestricting_ThenUnauthorized()
    {
        var response = await Gateway.PostAsync<DataOutput<RestrictProcessingCommandOutput?>>(
            "/api/auth/processing-restriction",
            new { ground = (int)RestrictionGrounds.AccuracyContested });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
