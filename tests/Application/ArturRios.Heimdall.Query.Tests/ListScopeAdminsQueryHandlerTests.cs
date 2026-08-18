using ArturRios.Data.Relational.Core.Entities;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for ListScopeAdminsQueryHandler (UC-07 read d, FR-PE-12): every live ScopeAdmin,
// paginated and filterable, projected to three fields. Covers the role filter, deleted exclusion,
// name/email filters, the excludeOwnersOfScopeId exclusion and its ownership gate (an unknown
// scope → ScopeNotFound, a non-owning Scope Admin → NotScopeOwner, a System Admin bypassing),
// exclusion before pagination, and the ordering tiebreaker.
public class ListScopeAdminsQueryHandlerTests
{
    private static Scope Scope(long id, bool isDeleted = false) =>
        new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}", IsDeleted = isDeleted };

    private static Person Admin(long id, string name, string email, bool isDeleted = false, Scope? owns = null)
    {
        var person = new Person
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            Name = name,
            Email = email,
            RoleId = (long)Roles.ScopeAdmin,
            IsDeleted = isDeleted
        };

        if (owns is not null)
        {
            person.ScopeOwnerships = [new ScopeOwner { ScopeId = owns.Id, Scope = owns }];
        }

        return person;
    }

    private static Person NonAdmin(long id, Roles role) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"person-{id}",
        Email = $"person-{id}@test.local",
        RoleId = (long)role
    };

    private static IScopeOwnershipChecker Ownership(bool allowed)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);

        return checker.Object;
    }

    private static async Task<AsyncFakeRepository<T>> RepositoryWith<T>(params T[] items) where T : Entity
    {
        var repository = new AsyncFakeRepository<T>();

        foreach (var item in items)
        {
            await repository.CreateAsync(item);
        }

        return repository;
    }

    private static ListScopeAdminsQuery Query(Guid? excludeOwnersOfScopeId = null, int pageSize = 10) => new()
    {
        PageNumber = 1,
        PageSize = pageSize,
        ExcludeOwnersOfScopeId = excludeOwnersOfScopeId,
        ActingPersonId = Guid.NewGuid(),
        ActingRole = (int)Roles.SystemAdmin
    };

    private static ListScopeAdminsQueryHandler HandlerFor(
        AsyncFakeRepository<Scope> scopes, AsyncFakeRepository<Person> persons, bool ownershipAllowed = true) =>
        new(scopes, persons, Ownership(ownershipAllowed), new ListScopeAdminsQueryValidator());

    [UnitFact]
    public async Task GivenMixedRoles_WhenHandlingListScopeAdmins_ThenOnlyScopeAdminsAreReturned()
    {
        // Given two Scope Admins, one User, and one System Admin
        var ana = Admin(10, "Ana", "ana@test.local");
        var bruno = Admin(11, "Bruno", "bruno@test.local");
        var scopes = await RepositoryWith<Scope>();
        var persons = await RepositoryWith(ana, bruno, NonAdmin(12, Roles.User), NonAdmin(13, Roles.SystemAdmin));
        var handler = HandlerFor(scopes, persons);

        // When
        var output = await handler.HandleAsync(Query());

        // Then
        Assert.True(output.Success);
        Assert.Equal(2, output.TotalItems);
        Assert.Equal([ana.PublicId, bruno.PublicId], output.Data!.Select(x => x.Id));
        Assert.Contains(PersonMessages.PersonsRetrievedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenDeletedScopeAdmin_WhenHandlingListScopeAdmins_ThenItIsExcluded()
    {
        // Given one live and one logically deleted Scope Admin — a deleted admin is never a valid
        // owner, so offering them in a picker could only produce a failed submission
        var ana = Admin(10, "Ana", "ana@test.local");
        var scopes = await RepositoryWith<Scope>();
        var persons = await RepositoryWith(ana, Admin(11, "Bruno", "bruno@test.local", isDeleted: true));
        var handler = HandlerFor(scopes, persons);

        // When
        var output = await handler.HandleAsync(Query());

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal([ana.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenNameFilter_WhenHandlingListScopeAdmins_ThenMatchesCaseInsensitiveSubstring()
    {
        // Given admins whose names differ in case
        var ana = Admin(10, "Ana Silva", "ana@test.local");
        var scopes = await RepositoryWith<Scope>();
        var persons = await RepositoryWith(ana, Admin(11, "Bruno", "bruno@test.local"));
        var handler = HandlerFor(scopes, persons);
        var query = Query();
        query.Name = "SILV";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal([ana.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenEmailFilter_WhenHandlingListScopeAdmins_ThenMatchesCaseInsensitiveSubstring()
    {
        // Given admins on different domains
        var ana = Admin(10, "Ana", "ana@heimdall.test");
        var scopes = await RepositoryWith<Scope>();
        var persons = await RepositoryWith(ana, Admin(11, "Bruno", "bruno@other.test"));
        var handler = HandlerFor(scopes, persons);
        var query = Query();
        query.Email = "HEIMDALL";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal([ana.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenExcludeOwnersOfScope_WhenHandlingListScopeAdmins_ThenCurrentOwnersAreRemoved()
    {
        // Given a scope whose owner is one of three admins (UI-14 AF-14c)
        var scope = Scope(1);
        var owner = Admin(10, "Ana", "ana@test.local", owns: scope);
        var bruno = Admin(11, "Bruno", "bruno@test.local");
        var carla = Admin(12, "Carla", "carla@test.local");
        var scopes = await RepositoryWith(scope);
        var persons = await RepositoryWith(owner, bruno, carla);
        var handler = HandlerFor(scopes, persons);

        // When
        var output = await handler.HandleAsync(Query(excludeOwnersOfScopeId: scope.PublicId));

        // Then
        Assert.Equal(2, output.TotalItems);
        Assert.Equal([bruno.PublicId, carla.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenExcludedOwnerAndFullPage_WhenHandlingListScopeAdmins_ThenPageIsNotShortened()
    {
        // Given four admins, one of them already an owner, and a page size of three: the exclusion
        // must happen before pagination, or the page comes back with two
        var scope = Scope(1);
        var scopes = await RepositoryWith(scope);
        var persons = await RepositoryWith(
            Admin(10, "Ana", "ana@test.local", owns: scope),
            Admin(11, "Bruno", "bruno@test.local"),
            Admin(12, "Carla", "carla@test.local"),
            Admin(13, "Diego", "diego@test.local"));
        var handler = HandlerFor(scopes, persons);

        // When
        var output = await handler.HandleAsync(Query(excludeOwnersOfScopeId: scope.PublicId, pageSize: 3));

        // Then
        Assert.Equal(3, output.TotalItems);
        Assert.Equal(3, output.Data!.Count());
    }

    [UnitFact]
    public async Task GivenUnknownScopeToExclude_WhenHandlingListScopeAdmins_ThenReturnsScopeNotFound()
    {
        // Given no such scope
        var scopes = await RepositoryWith<Scope>();
        var persons = await RepositoryWith(Admin(10, "Ana", "ana@test.local"));
        var handler = HandlerFor(scopes, persons);

        // When
        var output = await handler.HandleAsync(Query(excludeOwnersOfScopeId: Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedScopeToExclude_WhenHandlingListScopeAdmins_ThenReturnsScopeNotFound()
    {
        // Given a logically deleted scope
        var scope = Scope(1, isDeleted: true);
        var scopes = await RepositoryWith(scope);
        var persons = await RepositoryWith(Admin(10, "Ana", "ana@test.local"));
        var handler = HandlerFor(scopes, persons);

        // When
        var output = await handler.HandleAsync(Query(excludeOwnersOfScopeId: scope.PublicId));

        // Then
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenActorNotOwningExcludedScope_WhenHandlingListScopeAdmins_ThenReturnsNotScopeOwner()
    {
        // Given a Scope Admin naming a scope they do not own. Without this gate, running the query
        // with and without the parameter and diffing enumerates any scope's owners.
        var scope = Scope(1);
        var scopes = await RepositoryWith(scope);
        var persons = await RepositoryWith(Admin(10, "Ana", "ana@test.local", owns: scope));
        var handler = HandlerFor(scopes, persons, ownershipAllowed: false);

        // When
        var output = await handler.HandleAsync(Query(excludeOwnersOfScopeId: scope.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotScopeOwner, output.Errors);
    }

    [UnitFact]
    public async Task GivenNoScopeToExclude_WhenHandlingListScopeAdmins_ThenOwnershipIsNotChecked()
    {
        // Given a caller passing no excludeOwnersOfScopeId (UI-11, where no scope exists yet):
        // the unfiltered listing is open to both administrator roles
        var ana = Admin(10, "Ana", "ana@test.local");
        var scopes = await RepositoryWith<Scope>();
        var persons = await RepositoryWith(ana);
        var handler = HandlerFor(scopes, persons, ownershipAllowed: false);
        var query = Query();
        query.ActingRole = (int)Roles.ScopeAdmin;

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.True(output.Success);
        Assert.Equal([ana.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenAdminsSharingAName_WhenHandlingListScopeAdmins_ThenOrderIsDeterministic()
    {
        // Given two admins with the same name: the identifier tiebreaker is what stops one of them
        // appearing on two pages while the other appears on none
        var first = Admin(10, "Ana Silva", "ana1@test.local");
        var second = Admin(11, "Ana Silva", "ana2@test.local");
        var scopes = await RepositoryWith<Scope>();
        var persons = await RepositoryWith(second, first);
        var handler = HandlerFor(scopes, persons);

        // When
        var output = await handler.HandleAsync(Query());

        // Then
        var expected = new[] { first.PublicId, second.PublicId }.OrderBy(x => x);
        Assert.Equal(expected, output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenPageSizeAboveTheBound_WhenHandlingListScopeAdmins_ThenReturnsValidationError()
    {
        // Given NFR-10's page-size bound exceeded
        var scopes = await RepositoryWith<Scope>();
        var persons = await RepositoryWith(Admin(10, "Ana", "ana@test.local"));
        var handler = HandlerFor(scopes, persons);
        var query = Query(pageSize: 500);

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PaginationMessages.InvalidPageSize, output.Errors);
    }
}
