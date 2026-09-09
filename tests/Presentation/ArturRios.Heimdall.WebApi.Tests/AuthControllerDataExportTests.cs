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

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for POST /api/auth/data-export (UC-41) over the real pipeline. The behaviours
// that matter end to end: the subject is the token's, a Google User gets their own copy, the
// response is not cacheable, and an anonymous caller gets nothing.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerDataExportTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private async Task<Person> SeedPersonAsync()
    {
        await using var context = db.CreateContext();

        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Ada Lovelace",
            Email = $"export-{Guid.NewGuid():N}@functional.test",
            PasswordHash = Hash.EncodeWithRandomSalt("Str0ng-Export-Pass!", out var salt),
            Salt = salt,
            RoleId = (long)Roles.User,
            EmailVerified = true,
            LegalBasis = (int)LegalBases.ContractPerformance,
            PrivacyNoticeVersion = "1.0",
            BasisRecordedAt = DateTime.UtcNow
        };

        context.Persons.Add(person);
        await context.SaveChangesAsync();

        return person;
    }

    [FunctionalFact]
    public async Task GivenAPerson_WhenExporting_ThenTheirOwnCopyIsReturned()
    {
        var person = await SeedPersonAsync();
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        var response = await Gateway.PostAsync<DataOutput<DataExportCommandOutput?>>(
            "/api/auth/data-export", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var document = response.Body?.Data;

        Assert.NotNull(document);
        Assert.Equal(person.PublicId, document.Subject.Id);
        Assert.Equal("Ada Lovelace", document.Subject.Name);

        // The context Art. 15 asks for, not just the rows
        Assert.NotEmpty(document.Recipients);
        Assert.NotEmpty(document.Retention);
        Assert.NotEmpty(document.Withheld);
        Assert.Equal((int)LegalBases.ContractPerformance, document.Processing.LegalBasis);
    }

    [FunctionalFact]
    public async Task GivenAnExport_WhenReturned_ThenItIsNotCacheable()
    {
        // A document containing everything about a person must never sit in a proxy or browser
        // cache. It is why the endpoint is a POST at all.
        var person = await SeedPersonAsync();
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        var response = await Gateway.PostAsync<DataOutput<DataExportCommandOutput?>>(
            "/api/auth/data-export", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenExporting_ThenUnauthorized()
    {
        var response = await Gateway.PostAsync<DataOutput<DataExportCommandOutput?>>(
            "/api/auth/data-export", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenASystemAdmin_WhenExporting_ThenTheyGetTheirOwnDataAndNobodyElses()
    {
        // There is no shape of this request that names another identity, so the strongest role in
        // the system gets exactly what every other caller gets: their own copy.
        var person = await SeedPersonAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await Gateway.PostAsync<DataOutput<DataExportCommandOutput?>>(
            "/api/auth/data-export", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(person.PublicId, response.Body?.Data?.Subject.Id);
        Assert.Equal(
            PostgresFixture.StandInPersonIds[Roles.SystemAdmin], response.Body?.Data?.Subject.Id);
    }
}
