using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Auditing;
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

// Functional tests for POST /api/scopes/{scopeId}/owners/{personId} (UC-21, FR-SC-08/FR-SC-09): the
// main flow for a System Admin, an existing owner's invitation instead (FR-SC-14: 202, idempotent while
// open, audited with the invitation as target), AF-21a (scope unknown or logically deleted), AF-21b (person unknown, logically deleted, or not a ScopeAdmin), AF-21c (a Scope Admin
// who owns a different scope), AF-21d (a repeated call is an idempotent 200 that does not duplicate
// the row), and the framework flows the actor list produces (403 for a User, 401 unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class PersonControllerAddScopeOwnerTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid scopeId, Guid personId) => $"/api/scopes/{scopeId}/owners/{personId}";

    /// <summary>
    ///     The request carries no body — both identifiers are route values — but
    ///     <c>HttpGateway.PostAsync</c> takes a payload, so an empty object is sent and the action,
    ///     which binds no body, ignores it.
    /// </summary>
    private Task<HttpOutput<DataOutput<AddScopeOwnerCommandOutput?>?>> AddOwnerAsync(Guid scopeId, Guid personId) =>
        Gateway.PostAsync<DataOutput<AddScopeOwnerCommandOutput?>>(Route(scopeId, personId), new { });

    private async Task<int> OwnershipCountAsync(Scope scope, Person person)
    {
        await using var context = db.CreateContext();
        return await context.ScopeOwners.CountAsync(so => so.ScopeId == scope.Id && so.PersonId == person.Id);
    }

    private async Task<List<ScopeOwnerInvitation>> InvitationsOfAsync(Scope scope, Person person)
    {
        await using var context = db.CreateContext();
        return await context.ScopeOwnerInvitations.AsNoTracking()
            .Where(x => x.ScopeId == scope.Id && x.InviteePersonId == person.Id)
            .ToListAsync();
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenPostScopeOwner_ThenCreatedAndRowExists()
    {
        // Given a scope and a ScopeAdmin who does not own it yet (UC-21 main flow)
        var scope = await Seeds.ScopeAsync(db);
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin" });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await AddOwnerAsync(scope.PublicId, person.PublicId);

        // Then — response
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(scope.PublicId, response.Body?.Data?.ScopeId);
        Assert.Equal(person.PublicId, response.Body?.Data?.PersonId);
        Assert.False(response.Body?.Data?.AlreadyOwner);
        Assert.Contains(PersonMessages.ScopeOwnerAddedSuccessfully, response.Body!.Messages);

        // Then — database state: the ownership, and no invitation
        Assert.Equal(1, await OwnershipCountAsync(scope, person));
        Assert.False(response.Body.Data!.Invited);
        Assert.Empty(await InvitationsOfAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenExistingOwner_WhenPostScopeOwner_ThenAcceptedAndOnlyAnInvitationExists()
    {
        // Given a Scope Admin who already owns the scope adding a co-owner (FR-SC-14)
        var scope = await Seeds.ScopeAsync(db);
        var actor = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin" });
        Authorize(TestTokens.For(actor.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await AddOwnerAsync(scope.PublicId, person.PublicId);

        // Then — invited, not added
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains(ScopeInvitationMessages.InvitationSent, response.Body!.Messages);
        Assert.True(response.Body.Data!.Invited);
        Assert.Equal(0, await OwnershipCountAsync(scope, person));
        var invitation = Assert.Single(await InvitationsOfAsync(scope, person));
        Assert.Equal(response.Body.Data.InvitationId, invitation.PublicId);
        Assert.Equal(actor.Id, invitation.InvitedByPersonId);
        Assert.Null(invitation.EndedAt);
    }

    [FunctionalFact]
    public async Task GivenExistingOwnerInvitingTwice_WhenPostScopeOwner_ThenTheSameInvitationIsReturned()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db);
        var actor = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin" });
        Authorize(TestTokens.For(actor.PublicId, (int)Roles.ScopeAdmin));
        var first = await AddOwnerAsync(scope.PublicId, person.PublicId);

        // When
        var second = await AddOwnerAsync(scope.PublicId, person.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(first.Body!.Data!.InvitationId, second.Body!.Data!.InvitationId);
        Assert.Single(await InvitationsOfAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenExistingOwner_WhenAnInvitationIsOpened_ThenItIsAudited()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db);
        var actor = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin" });
        Authorize(TestTokens.For(actor.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await AddOwnerAsync(scope.PublicId, person.PublicId);

        // Then — the command's own entry, and the invitation's, which names the invitation
        await using var context = db.CreateContext();
        var entries = await context.AuditLogs.AsNoTracking()
            .Where(x => x.ActorPersonId == actor.PublicId && x.Succeeded)
            .Select(x => new { x.Action, x.TargetId })
            .ToListAsync();
        Assert.Contains(entries, x => x.Action == nameof(AddScopeOwnerCommand));
        var created = Assert.Single(entries, x => x.Action == ScopeOwnerInvitationAuditActions.Created);
        Assert.Equal(response.Body!.Data!.InvitationId, created.TargetId);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminOfAnotherScope_WhenPostScopeOwner_ThenForbidden()
    {
        // Given a Scope Admin who owns some other scope (AF-21c)
        var scope = await Seeds.ScopeAsync(db);
        var otherScope = await Seeds.ScopeAsync(db);
        var actor = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [otherScope] });
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin" });
        Authorize(TestTokens.For(actor.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await AddOwnerAsync(scope.PublicId, person.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenPostScopeOwner_ThenForbidden()
    {
        // Given a caller holding the User role — refused by the attribute, before any handler runs
        var scope = await Seeds.ScopeAsync(db);
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin" });
        var user = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "user", MemberOf = scope });
        Authorize(TestTokens.For(user.PublicId, (int)Roles.User));

        // When
        var response = await AddOwnerAsync(scope.PublicId, person.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenUnknownScope_WhenPostScopeOwner_ThenNotFound()
    {
        // AF-21a
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin" });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await AddOwnerAsync(Guid.NewGuid(), person.PublicId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(PersonMessages.ScopeNotFound, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedScope_WhenPostScopeOwner_ThenNotFound()
    {
        // AF-21a treats a logically deleted scope as absent
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { IsDeleted = true });
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin" });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await AddOwnerAsync(scope.PublicId, person.PublicId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenUnknownPerson_WhenPostScopeOwner_ThenBadRequest()
    {
        // AF-21b
        var scope = await Seeds.ScopeAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await AddOwnerAsync(scope.PublicId, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(PersonMessages.PersonNotValidScopeAdmin, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedPerson_WhenPostScopeOwner_ThenBadRequest()
    {
        // AF-21b — a deleted person can no longer authenticate, so the ownership would be unusable
        var scope = await Seeds.ScopeAsync(db);
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", IsDeleted = true });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await AddOwnerAsync(scope.PublicId, person.PublicId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenUserPerson_WhenPostScopeOwner_ThenBadRequest()
    {
        // AF-21b — only a ScopeAdmin may own a scope (FR-SC-08); promoting a User is UC-23
        var scope = await Seeds.ScopeAsync(db);
        var person = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "user", MemberOf = scope });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await AddOwnerAsync(scope.PublicId, person.PublicId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenPersonAddedTwice_WhenPostScopeOwner_ThenSecondCallIsOkAndRowIsNotDuplicated()
    {
        // AF-21d: the repeat is an idempotent 200, distinct from the main flow's 201, and the
        // composite key is never written twice
        var scope = await Seeds.ScopeAsync(db);
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin" });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var first = await AddOwnerAsync(scope.PublicId, person.PublicId);
        var second = await AddOwnerAsync(scope.PublicId, person.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(second.Body?.Data?.AlreadyOwner);
        Assert.Contains(PersonMessages.AlreadyScopeOwner, second.Body!.Messages);
        Assert.Equal(1, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPostScopeOwner_ThenUnauthorized()
    {
        var scope = await Seeds.ScopeAsync(db);
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin" });

        var response = await AddOwnerAsync(scope.PublicId, person.PublicId);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await OwnershipCountAsync(scope, person));
    }
}
