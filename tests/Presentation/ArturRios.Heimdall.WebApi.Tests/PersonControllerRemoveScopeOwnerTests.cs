using System.Net;
using ArturRios.Configuration.Enums;
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

// Functional tests for DELETE /api/scopes/{scopeId}/owners/{personId} (UC-22, FR-SC-08/FR-SC-10): the
// main flow for a System Admin and for a co-owner, AF-22a (scope unknown or logically deleted; person
// unknown or not an owner of this scope), AF-22b (the scope's last live owner, NFR-12), AF-22c (a
// Scope Admin who owns a different scope), the non-idempotent repeat, and the framework flows the
// actor list produces (403 for a User, 401 unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class PersonControllerRemoveScopeOwnerTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid scopeId, Guid personId) => $"/api/scopes/{scopeId}/owners/{personId}";

    private Task<HttpOutput<DataOutput<RemoveScopeOwnerCommandOutput?>?>> RemoveOwnerAsync(
        Guid scopeId, Guid personId) =>
        Gateway.DeleteAsync<DataOutput<RemoveScopeOwnerCommandOutput?>>(Route(scopeId, personId));

    private async Task<int> OwnershipCountAsync(Scope scope, Person person)
    {
        await using var context = db.CreateContext();
        return await context.ScopeOwners.CountAsync(so => so.ScopeId == scope.Id && so.PersonId == person.Id);
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenDeleteScopeOwner_ThenOkAndRowIsGone()
    {
        // Given a scope with two owners (UC-22 main flow)
        var scope = await Seeds.ScopeAsync(db);
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        var coOwner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await RemoveOwnerAsync(scope.PublicId, person.PublicId);

        // Then — response carries the two public identifiers
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(scope.PublicId, response.Body?.Data?.ScopeId);
        Assert.Equal(person.PublicId, response.Body?.Data?.PersonId);
        Assert.Contains(PersonMessages.ScopeOwnerRemovedSuccessfully, response.Body!.Messages);

        // Then — database state: the row is gone and the co-owner's survives
        Assert.Equal(0, await OwnershipCountAsync(scope, person));
        Assert.Equal(1, await OwnershipCountAsync(scope, coOwner));
    }

    [FunctionalFact]
    public async Task GivenCoOwner_WhenDeleteScopeOwner_ThenOkAndRowIsGone()
    {
        // Given a Scope Admin removing a co-owner of a scope they own (FR-SC-10)
        var scope = await Seeds.ScopeAsync(db);
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        var actor = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        Authorize(TestTokens.For(actor.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await RemoveOwnerAsync(scope.PublicId, person.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await OwnershipCountAsync(scope, person));
        Assert.Equal(1, await OwnershipCountAsync(scope, actor));
    }

    [FunctionalFact]
    public async Task GivenScopeAdminOfAnotherScope_WhenDeleteScopeOwner_ThenForbidden()
    {
        // Given a Scope Admin who owns some other scope (AF-22c)
        var scope = await Seeds.ScopeAsync(db);
        var otherScope = await Seeds.ScopeAsync(db);
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        var actor = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [otherScope] });
        Authorize(TestTokens.For(actor.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await RemoveOwnerAsync(scope.PublicId, person.PublicId);

        // Then — refused and the row survives
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenDeleteScopeOwner_ThenForbidden()
    {
        // Given a caller holding the User role — refused by the attribute, before any handler runs
        var scope = await Seeds.ScopeAsync(db);
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        var user = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "user", MemberOf = scope });
        Authorize(TestTokens.For(user.PublicId, (int)Roles.User));

        // When
        var response = await RemoveOwnerAsync(scope.PublicId, person.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenUnknownScope_WhenDeleteScopeOwner_ThenNotFound()
    {
        // AF-22a
        var scope = await Seeds.ScopeAsync(db);
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await RemoveOwnerAsync(Guid.NewGuid(), person.PublicId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(PersonMessages.ScopeNotFound, response.Body!.Errors);
        Assert.Equal(1, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedScope_WhenDeleteScopeOwner_ThenNotFound()
    {
        // AF-22a treats a logically deleted scope as absent
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { IsDeleted = true });
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await RemoveOwnerAsync(scope.PublicId, person.PublicId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(PersonMessages.ScopeNotFound, response.Body!.Errors);
        Assert.Equal(1, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenUnknownPerson_WhenDeleteScopeOwner_ThenNotFound()
    {
        // AF-22a
        var scope = await Seeds.ScopeAsync(db);
        await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await RemoveOwnerAsync(scope.PublicId, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(PersonMessages.PersonNotScopeOwner, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenPersonNotOwningTheScope_WhenDeleteScopeOwner_ThenNotFound()
    {
        // AF-22a — a ScopeAdmin who owns a different scope holds no row here; the target answer is a
        // 404 distinct from the caller-facing 403 of AF-22c
        var scope = await Seeds.ScopeAsync(db);
        var otherScope = await Seeds.ScopeAsync(db);
        await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [otherScope] });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await RemoveOwnerAsync(scope.PublicId, person.PublicId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(PersonMessages.PersonNotScopeOwner, response.Body!.Errors);
        Assert.Equal(1, await OwnershipCountAsync(otherScope, person));
    }

    [FunctionalFact]
    public async Task GivenSoleOwner_WhenDeleteScopeOwner_ThenConflictAndRowSurvives()
    {
        // AF-22b, NFR-12: the scope's only owner may not be removed
        var scope = await Seeds.ScopeAsync(db);
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await RemoveOwnerAsync(scope.PublicId, person.PublicId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(PersonMessages.ScopeWouldLoseLastOwner, response.Body!.Errors);
        Assert.Equal(1, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenOwnerRemovedTwice_WhenDeleteScopeOwner_ThenSecondCallIsNotFound()
    {
        // The removal is not idempotent: the repeat finds no ownership row and answers AF-22a, the
        // same contrast UC-19 and UC-20 pin between the logical and the hard application deletion
        var scope = await Seeds.ScopeAsync(db);
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var first = await RemoveOwnerAsync(scope.PublicId, person.PublicId);
        var second = await RemoveOwnerAsync(scope.PublicId, person.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Contains(PersonMessages.PersonNotScopeOwner, second.Body!.Errors);
        Assert.Equal(0, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenDeleteScopeOwner_ThenUnauthorized()
    {
        var scope = await Seeds.ScopeAsync(db);
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });

        var response = await RemoveOwnerAsync(scope.PublicId, person.PublicId);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, await OwnershipCountAsync(scope, person));
    }
}
