using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Hashing;
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
    private const string Password = "Str0ng-Restrict-Pass!";

    private async Task<Person> SeedPersonAsync(DateTime? restrictedAt = null)
    {
        await using var context = db.CreateContext();

        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Ada",
            Email = $"restrict-{Guid.NewGuid():N}@functional.test",
            PasswordHash = Hash.EncodeWithRandomSalt(Password, out var salt),
            Salt = salt,
            RoleId = (long)Roles.User,
            EmailVerified = true,
            ProcessingRestrictedAt = restrictedAt,
            RestrictionGround = restrictedAt is null ? null : (int)RestrictionGrounds.AccuracyContested
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

    [FunctionalFact]
    public async Task GivenAPerson_WhenRestrictingProcessing_ThenNothingIsDeleted()
    {
        var person = await SeedPersonAsync();
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
        var person = await SeedPersonAsync();
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
        // request, so a token issued before it does not keep working.
        var person = await SeedPersonAsync(restrictedAt: DateTime.UtcNow);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        var response = await Gateway.GetAsync<DataOutput<object>>($"/api/persons/{person.PublicId}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenARestrictedPerson_WhenTheyLogIn_ThenTheyAreRefused()
    {
        // Answered with the ordinary invalid-credentials message, so the endpoint cannot be used to
        // discover that an account is under dispute
        var person = await SeedPersonAsync(restrictedAt: DateTime.UtcNow);

        var response = await Gateway.PostAsync<DataOutput<object>>(
            "/api/auth/login", new { email = person.Email, password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenTheSubject_WhenLiftingTheirOwn_ThenItIsLiftedWithoutNotification()
    {
        var person = await SeedPersonAsync(restrictedAt: DateTime.UtcNow.AddDays(-1));

        // A restricted identity cannot act on a token, so the lift is exercised as the System Admin
        // path would reach it — see the unit tests for the subject-lifts-own path, which cannot be
        // driven over HTTP while the liveness filter refuses the token.
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
        var person = await SeedPersonAsync(restrictedAt: DateTime.UtcNow.AddDays(-1));
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
