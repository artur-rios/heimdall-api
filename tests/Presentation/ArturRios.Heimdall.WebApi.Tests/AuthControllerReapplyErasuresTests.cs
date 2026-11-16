using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for POST /api/auth/erasure-reconciliation (NFR-25), the restore runbook's step that re-applies
// the erasures a database restore brought back. ReapplyErasuresCommandHandlerTests and IdentityAnonymisationTests
// cover what the handler writes; these cover the route itself: System Admin only, the status each outcome maps to,
// and the body a runbook reads its counts from.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerReapplyErasuresTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Route = "/api/auth/erasure-reconciliation";

    private Task<HttpOutput<DataOutput<ReapplyErasuresCommandOutput?>?>> ReapplyAsync(
        params Guid[] subjectIds) =>
        Gateway.PostAsync<DataOutput<ReapplyErasuresCommandOutput?>>(Route, new { subjectIds });

    [FunctionalFact]
    public async Task GivenARestoredIdentityAndAnUnknownId_WhenASystemAdminReapplies_ThenOneIsAnonymisedAndOneNotFound()
    {
        // Given a live person a restore brought back, and an id from the ledger that names nobody here
        var restored = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Name = "Ada", EmailPrefix = "ada" });
        var unknown = Guid.NewGuid();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await ReapplyAsync(restored.PublicId, unknown);

        // Then the counts the runbook reads
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(ErasureMessages.ErasuresReapplied, response.Body!.Messages);
        Assert.Equal(1, response.Body.Data!.Anonymised);
        Assert.Equal(0, response.Body.Data.AlreadyAnonymised);
        Assert.Equal([unknown], response.Body.Data.NotFound);

        // Then the identity is anonymised in the database
        await using var context = db.CreateContext();
        var stored = await context.Persons.AsNoTracking().SingleAsync(person => person.Id == restored.Id);
        Assert.NotNull(stored.AnonymisedAt);
        Assert.Equal(IdentityAnonymiser.AnonymisedName, stored.Name);
    }

    [FunctionalFact]
    public async Task GivenAnIdentityAlreadyReapplied_WhenASystemAdminReappliesAgain_ThenItIsCountedAsAlreadyAnonymised()
    {
        // Given a runbook step run twice
        var restored = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "twice" });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        Assert.Equal(HttpStatusCode.OK, (await ReapplyAsync(restored.PublicId)).StatusCode);

        // When
        var response = await ReapplyAsync(restored.PublicId);

        // Then the second run changes nothing and says so
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, response.Body!.Data!.Anonymised);
        Assert.Equal(1, response.Body.Data.AlreadyAnonymised);
        Assert.Empty(response.Body.Data.NotFound);
    }

    [FunctionalFact]
    public async Task GivenNoSubjects_WhenASystemAdminReapplies_ThenBadRequest()
    {
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await ReapplyAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([ErasureMessages.NoSubjectsToReapply], response.Body!.Errors);
    }

    [FunctionalTheory]
    [InlineData(Roles.ScopeAdmin)]
    [InlineData(Roles.User)]
    public async Task GivenACallerWhoIsNotASystemAdmin_WhenReapplying_ThenForbiddenAndNothingChanges(Roles role)
    {
        // Given
        var restored = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "kept" });
        Authorize(TestTokens.ForRole((int)role));

        // When
        var response = await ReapplyAsync(restored.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = db.CreateContext();
        var stored = await context.Persons.AsNoTracking().SingleAsync(person => person.Id == restored.Id);
        Assert.Null(stored.AnonymisedAt);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenReapplying_ThenUnauthorized()
    {
        var response = await ReapplyAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
