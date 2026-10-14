using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for ListSystemAdminsQueryHandler (UC-07 read e, FR-PE-13): every live SystemAdmin,
// paginated and filterable, projected to three fields. Covers the role filter, deleted and
// restricted exclusion, name/email filters, the ordering tiebreaker, pagination, and the NFR-10
// validation failures.
public class ListSystemAdminsQueryHandlerTests
{
    private static Person Person(long id, string name, string email, Roles role = Roles.SystemAdmin, bool isDeleted = false) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = name,
        Email = email,
        RoleId = (long)role,
        IsDeleted = isDeleted
    };

    private static async Task<AsyncFakeRepository<Person, long>> RepositoryWith(params Person[] persons)
    {
        var repository = new AsyncFakeRepository<Person, long>();

        foreach (var person in persons)
        {
            await repository.CreateAsync(person);
        }

        return repository;
    }

    private static ListSystemAdminsQuery Query(int pageNumber = 1, int pageSize = 10) => new()
    {
        PageNumber = pageNumber,
        PageSize = pageSize,
        ActingPersonId = Guid.NewGuid(),
        ActingRole = (int)Roles.SystemAdmin
    };

    private static ListSystemAdminsQueryHandler HandlerFor(AsyncFakeRepository<Person, long> persons) =>
        new(persons, new ListSystemAdminsQueryValidator());

    [UnitFact]
    public async Task GivenMixedRoles_WhenHandlingListSystemAdmins_ThenOnlySystemAdminsAreReturned()
    {
        // Given two System Admins, a Scope Admin, and a User
        var ana = Person(10, "Ana", "ana@test.local");
        var bruno = Person(11, "Bruno", "bruno@test.local");
        var persons = await RepositoryWith(
            ana, bruno, Person(12, "Carla", "carla@test.local", Roles.ScopeAdmin),
            Person(13, "Diego", "diego@test.local", Roles.User));
        var handler = HandlerFor(persons);

        // When
        var output = await handler.HandleAsync(Query());

        // Then
        Assert.True(output.Success);
        Assert.Equal(2, output.TotalItems);
        Assert.Equal([ana.PublicId, bruno.PublicId], output.Data!.Select(x => x.Id));
        Assert.Contains(PersonMessages.PersonsRetrievedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenSystemAdmin_WhenHandlingListSystemAdmins_ThenOnlyIdNameAndEmailAreProjected()
    {
        // Given one System Admin
        var ana = Person(10, "Ana", "ana@test.local");
        var persons = await RepositoryWith(ana);
        var handler = HandlerFor(persons);

        // When
        var output = await handler.HandleAsync(Query());

        // Then
        var listed = Assert.Single(output.Data!);
        Assert.Equal(ana.PublicId, listed.Id);
        Assert.Equal("Ana", listed.Name);
        Assert.Equal("ana@test.local", listed.Email);
    }

    [UnitFact]
    public async Task GivenDeletedSystemAdmin_WhenHandlingListSystemAdmins_ThenItIsExcluded()
    {
        // Given one live and one logically deleted System Admin
        var ana = Person(10, "Ana", "ana@test.local");
        var persons = await RepositoryWith(ana, Person(11, "Bruno", "bruno@test.local", isDeleted: true));
        var handler = HandlerFor(persons);

        // When
        var output = await handler.HandleAsync(Query());

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal([ana.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenRestrictedSystemAdmin_WhenHandlingListSystemAdmins_ThenItIsWithheld()
    {
        // Given one System Admin under a restriction of processing (NFR-24)
        var ana = Person(10, "Ana", "ana@test.local");
        var bruno = Person(11, "Bruno", "bruno@test.local");
        bruno.ProcessingRestrictedAt = DateTime.UtcNow;
        var persons = await RepositoryWith(ana, bruno);
        var handler = HandlerFor(persons);

        // When
        var output = await handler.HandleAsync(Query());

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal([ana.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenNoSystemAdmins_WhenHandlingListSystemAdmins_ThenReturnsAnEmptyPage()
    {
        // Given only a Scope Admin
        var persons = await RepositoryWith(Person(10, "Ana", "ana@test.local", Roles.ScopeAdmin));
        var handler = HandlerFor(persons);

        // When
        var output = await handler.HandleAsync(Query());

        // Then
        Assert.True(output.Success);
        Assert.Equal(0, output.TotalItems);
        Assert.Empty(output.Data!);
    }

    [UnitFact]
    public async Task GivenNameFilter_WhenHandlingListSystemAdmins_ThenMatchesCaseInsensitiveSubstring()
    {
        // Given System Admins whose names differ in case
        var ana = Person(10, "Ana Silva", "ana@test.local");
        var persons = await RepositoryWith(ana, Person(11, "Bruno", "bruno@test.local"));
        var handler = HandlerFor(persons);
        var query = Query();
        query.Name = "SILV";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal([ana.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenEmailFilter_WhenHandlingListSystemAdmins_ThenMatchesCaseInsensitiveSubstring()
    {
        // Given System Admins on different domains
        var ana = Person(10, "Ana", "ana@heimdall.test");
        var persons = await RepositoryWith(ana, Person(11, "Bruno", "bruno@other.test"));
        var handler = HandlerFor(persons);
        var query = Query();
        query.Email = "HEIMDALL";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal([ana.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenSystemAdminsSharingAName_WhenHandlingListSystemAdmins_ThenOrderIsDeterministic()
    {
        // Given two System Admins with the same name: the identifier tiebreaker is what stops one of
        // them appearing on two pages while the other appears on none
        var first = Person(10, "Ana Silva", "ana1@test.local");
        var second = Person(11, "Ana Silva", "ana2@test.local");
        var persons = await RepositoryWith(second, first);
        var handler = HandlerFor(persons);

        // When
        var output = await handler.HandleAsync(Query());

        // Then
        var expected = new[] { first.PublicId, second.PublicId }.OrderBy(x => x);
        Assert.Equal(expected, output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenMoreSystemAdminsThanAPage_WhenHandlingListSystemAdmins_ThenReturnsTheRequestedPage()
    {
        // Given three System Admins and a page size of two
        var carla = Person(12, "Carla", "carla@test.local");
        var persons = await RepositoryWith(
            Person(10, "Ana", "ana@test.local"), Person(11, "Bruno", "bruno@test.local"), carla);
        var handler = HandlerFor(persons);

        // When
        var output = await handler.HandleAsync(Query(pageNumber: 2, pageSize: 2));

        // Then — the total counts every match; the second page holds the remainder
        Assert.Equal(3, output.TotalItems);
        Assert.Equal([carla.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenPageSizeAboveTheBound_WhenHandlingListSystemAdmins_ThenReturnsValidationError()
    {
        // Given NFR-10's page-size bound exceeded
        var persons = await RepositoryWith(Person(10, "Ana", "ana@test.local"));
        var handler = HandlerFor(persons);

        // When
        var output = await handler.HandleAsync(Query(pageSize: 500));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PaginationMessages.InvalidPageSize, output.Errors);
    }

    [UnitFact]
    public async Task GivenPageNumberBelowOne_WhenHandlingListSystemAdmins_ThenReturnsValidationError()
    {
        // Given NFR-10's page-number bound violated
        var persons = await RepositoryWith(Person(10, "Ana", "ana@test.local"));
        var handler = HandlerFor(persons);

        // When
        var output = await handler.HandleAsync(Query(pageNumber: 0));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PaginationMessages.InvalidPageNumber, output.Errors);
    }

    [UnitFact]
    public async Task GivenNameFilterLongerThanColumn_WhenHandlingListSystemAdmins_ThenReturnsFilterTooLongError()
    {
        // Given a name filter longer than Person.Name's own 200-character column, which could never match
        var persons = await RepositoryWith(Person(10, "Ana", "ana@test.local"));
        var handler = HandlerFor(persons);
        var query = Query();
        query.Name = new string('a', 201);

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PaginationMessages.FilterTooLong, output.Errors);
    }

    [UnitFact]
    public async Task GivenEmailFilterLongerThanColumn_WhenHandlingListSystemAdmins_ThenReturnsFilterTooLongError()
    {
        // Given an email filter longer than Person.Email's own 256-character column
        var persons = await RepositoryWith(Person(10, "Ana", "ana@test.local"));
        var handler = HandlerFor(persons);
        var query = Query();
        query.Email = new string('a', 257);

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PaginationMessages.FilterTooLong, output.Errors);
    }
}
