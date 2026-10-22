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

// Functional tests for UC-16 (Create Application): main flow for a System Admin and for an owning
// Scope Admin naming themself, AF-16a/b/c/d/e, the User-role refusal (FR-AP-03 gives a User nothing
// to create), the anonymous 401, and the duplicate-name boundary the design records.
[Collection(nameof(FunctionalCollection))]
public class ApplicationControllerCreateTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static CreateApplicationCommand Command(Guid ownerId, string? name = null) => new()
    {
        Name = name ?? $"app-{Guid.NewGuid():N}", OwnerId = ownerId
    };

    private static string Route(Guid scopeId) => $"/api/scopes/{scopeId}/applications";

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenPostApplications_ThenApplicationIsCreated()
    {
        // Given a scope with a ScopeAdmin owner who will own the application (FR-AP-03)
        var scope = await Seeds.ScopeAsync(db);
        var owner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var command = Command(owner.PublicId);

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            Route(scope.PublicId), command);

        // Then — response
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(command.Name, response.Body?.Data?.Name);
        Assert.Equal(scope.PublicId, response.Body?.Data?.ScopeId);
        Assert.Equal(owner.PublicId, response.Body?.Data?.OwnerId);

        // Then — the application row points at the scope and the owner, and is active
        await using var context = db.CreateContext();
        var application = await context.Applications.AsNoTracking()
            .FirstAsync(a => a.Name == command.Name);
        Assert.Equal(scope.Id, application.ScopeId);
        Assert.Equal(owner.Id, application.OwnerId);
        Assert.False(application.IsDeleted);
        Assert.Equal(response.Body?.Data?.Id, application.PublicId);
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdminNamingThemself_WhenPostApplications_ThenApplicationIsCreated()
    {
        // Given a ScopeAdmin who owns the scope, naming themself as owner (matrix: "self as owner")
        var scope = await Seeds.ScopeAsync(db);
        var admin = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin));
        var command = Command(admin.PublicId);

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            Route(scope.PublicId), command);

        // Then — response
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(admin.PublicId, response.Body?.Data?.OwnerId);

        // Then — the row is owned by the caller
        await using var context = db.CreateContext();
        var application = await context.Applications.AsNoTracking()
            .FirstAsync(a => a.Name == command.Name);
        Assert.Equal(admin.Id, application.OwnerId);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNamingACoOwner_WhenPostApplications_ThenForbiddenAndNothingIsCreated()
    {
        // Given a ScopeAdmin naming a co-owner of the same scope as owner (AF-16c). The co-owner
        // would satisfy FR-AP-03, so the refusal is about who asked.
        var scope = await Seeds.ScopeAsync(db);
        var caller = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        var coOwner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.ScopeAdmin));
        var command = Command(coOwner.PublicId);

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            Route(scope.PublicId), command);

        // Then — response and the absence of a row
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = db.CreateContext();
        Assert.False(await context.Applications.AnyAsync(a => a.Name == command.Name));
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenPostApplications_ThenForbidden()
    {
        // Given a caller holding the User role: FR-AP-03 lets them own nothing, so the endpoint's
        // [RoleRequirement] refuses them before the handler runs
        var scope = await Seeds.ScopeAsync(db);
        var caller = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "user", MemberOf = scope });
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.User));
        var command = Command(caller.PublicId);

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            Route(scope.PublicId), command);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = db.CreateContext();
        Assert.False(await context.Applications.AnyAsync(a => a.Name == command.Name));
    }

    [FunctionalFact]
    public async Task GivenOwnerWithUserRole_WhenPostApplications_ThenBadRequest()
    {
        // Given a User of the scope named as owner: FR-AP-03 restricts ownership to a ScopeAdmin who
        // owns the scope, so belonging to it is not enough (AF-16b)
        var scope = await Seeds.ScopeAsync(db);
        var owner = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "user", MemberOf = scope });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            Route(scope.PublicId), Command(owner.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenMissingScope_WhenPostApplications_ThenNotFound()
    {
        // Given a scope id nobody holds (AF-16a)
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            Route(Guid.NewGuid()), Command(Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedScope_WhenPostApplications_ThenNotFound()
    {
        // Given a logically deleted scope (AF-16a)
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { IsDeleted = true });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            Route(scope.PublicId), Command(Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwner_WhenPostApplications_ThenForbidden()
    {
        // Given a ScopeAdmin who does NOT own the scope (AF-16e)
        var scope = await Seeds.ScopeAsync(db);
        var owner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        var stranger = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin" });
        Authorize(TestTokens.For(stranger.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            Route(scope.PublicId), Command(owner.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnknownOwner_WhenPostApplications_ThenBadRequest()
    {
        // Given an owner id nobody holds (AF-16b)
        var scope = await Seeds.ScopeAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            Route(scope.PublicId), Command(Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenOwnerOfADifferentScope_WhenPostApplications_ThenBadRequest()
    {
        // Given a ScopeAdmin who owns another scope entirely (AF-16b)
        var scope = await Seeds.ScopeAsync(db);
        var otherScope = await Seeds.ScopeAsync(db);
        var stranger = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [otherScope] });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            Route(scope.PublicId), Command(stranger.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedOwner_WhenPostApplications_ThenBadRequest()
    {
        // Given a logically deleted owner of the scope (AF-16b)
        var scope = await Seeds.ScopeAsync(db);
        var owner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", IsDeleted = true, Owns = [scope] });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            Route(scope.PublicId), Command(owner.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenEmptyName_WhenPostApplications_ThenBadRequest()
    {
        // Given a command with no name (AF-16d)
        var scope = await Seeds.ScopeAsync(db);
        var owner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            Route(scope.PublicId), Command(owner.PublicId, name: string.Empty));

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPostApplications_ThenUnauthorized()
    {
        // Given no bearer token (precondition: the actor is authenticated)
        var scope = await Seeds.ScopeAsync(db);

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            Route(scope.PublicId), Command(Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDuplicateName_WhenPostApplications_ThenBothAreCreated()
    {
        // Given an application already registered under a name: no requirement makes names unique
        var scope = await Seeds.ScopeAsync(db);
        var owner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var command = Command(owner.PublicId);
        var first = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            Route(scope.PublicId), command);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // When posting the same name again
        var response = await Gateway.PostAsync<DataOutput<CreateApplicationCommandOutput?>>(
            Route(scope.PublicId), command);

        // Then — both exist, distinguished by their public identifiers
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotEqual(first.Body?.Data?.Id, response.Body?.Data?.Id);

        await using var context = db.CreateContext();
        Assert.Equal(2, await context.Applications.CountAsync(a => a.Name == command.Name));
    }
}
