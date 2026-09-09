using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for ListErasureRequestsQueryHandler (UC-43). The queue exists so a deadline nobody can
// see does not become a deadline nobody meets, so what it includes, what it hides, and how it
// orders are all load-bearing.
public class ListErasureRequestsQueryHandlerTests
{
    private static ListErasureRequestsQueryHandler Handler(
        AsyncFakeRepository<Person> persons, AsyncFakeRepository<GoogleUser> googleUsers) =>
        new(persons, googleUsers, new ListErasureRequestsQueryValidator());

    private static ListErasureRequestsQuery Query(bool overdueOnly = false) =>
        new() { PageNumber = 1, PageSize = 20, OverdueOnly = overdueOnly };

    private static async Task<Person> SeedPersonAsync(
        AsyncFakeRepository<Person> persons,
        DateTime? requestedAt,
        DateTime? dueAt,
        string? blockedReason = null,
        DateTime? anonymisedAt = null)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Ada Lovelace",
            Email = $"ada-{Guid.NewGuid():N}@test.local",
            ErasureRequestedAt = requestedAt,
            ErasureDueAt = dueAt,
            ErasureBlockedReason = blockedReason,
            AnonymisedAt = anonymisedAt
        };

        await persons.CreateAsync(person);

        return person;
    }

    [UnitFact]
    public async Task GivenOutstandingRequests_WhenListing_ThenTheyAreReturnedSoonestDeadlineFirst()
    {
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var later = await SeedPersonAsync(persons, DateTime.UtcNow, DateTime.UtcNow.AddDays(20));
        var sooner = await SeedPersonAsync(persons, DateTime.UtcNow, DateTime.UtcNow.AddDays(5));

        var output = await Handler(persons, googleUsers).HandleAsync(Query());

        Assert.True(output.Success);
        Assert.Equal(ErasureMessages.ErasureRequestsRetrieved, output.Messages.First());

        var results = output.Data!.ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal(sooner.PublicId, results[0].SubjectId);
        Assert.Equal(later.PublicId, results[1].SubjectId);
    }

    [UnitFact]
    public async Task GivenAnAlreadyAnonymisedRequest_WhenListing_ThenItIsNotOutstanding()
    {
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        await SeedPersonAsync(
            persons, DateTime.UtcNow.AddDays(-40), DateTime.UtcNow.AddDays(-10),
            anonymisedAt: DateTime.UtcNow.AddDays(-9));

        var output = await Handler(persons, googleUsers).HandleAsync(Query());

        Assert.Empty(output.Data!);
    }

    [UnitFact]
    public async Task GivenAnIdentityThatNeverAsked_WhenListing_ThenItIsNotIncluded()
    {
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        await SeedPersonAsync(persons, requestedAt: null, dueAt: null);

        var output = await Handler(persons, googleUsers).HandleAsync(Query());

        Assert.Empty(output.Data!);
    }

    [UnitFact]
    public async Task GivenAPassedDeadline_WhenListing_ThenTheRequestIsMarkedOverdue()
    {
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        await SeedPersonAsync(persons, DateTime.UtcNow.AddDays(-40), DateTime.UtcNow.AddDays(-10));

        var output = await Handler(persons, googleUsers).HandleAsync(Query());

        Assert.True(Assert.Single(output.Data!).Overdue);
    }

    [UnitFact]
    public async Task GivenOverdueOnly_WhenListing_ThenOnlyLateRequestsAreReturned()
    {
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var late = await SeedPersonAsync(persons, DateTime.UtcNow.AddDays(-40), DateTime.UtcNow.AddDays(-10));
        await SeedPersonAsync(persons, DateTime.UtcNow, DateTime.UtcNow.AddDays(20));

        var output = await Handler(persons, googleUsers).HandleAsync(Query(overdueOnly: true));

        Assert.Equal(late.PublicId, Assert.Single(output.Data!).SubjectId);
    }

    [UnitFact]
    public async Task GivenABlockedRequest_WhenListing_ThenTheReasonIsCarried()
    {
        // The blocked ones are why this queue exists: they need an owner transferred before the
        // pass can carry them out.
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        await SeedPersonAsync(
            persons, DateTime.UtcNow, DateTime.UtcNow.AddDays(30),
            blockedReason: ErasureMessages.BlockedByLastScopeOwnership);

        var output = await Handler(persons, googleUsers).HandleAsync(Query());

        Assert.Equal(ErasureMessages.BlockedByLastScopeOwnership, Assert.Single(output.Data!).BlockedReason);
    }

    [UnitFact]
    public async Task GivenAGoogleUsersRequest_WhenListing_ThenItAppearsAlongsideThePersons()
    {
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = "108124442542040415000",
            Name = "Ada",
            Email = "ada@test.local",
            ErasureRequestedAt = DateTime.UtcNow,
            ErasureDueAt = DateTime.UtcNow.AddDays(30)
        };
        await googleUsers.CreateAsync(googleUser);

        var output = await Handler(persons, googleUsers).HandleAsync(Query());

        var result = Assert.Single(output.Data!);

        Assert.Equal(googleUser.PublicId, result.SubjectId);
        Assert.True(result.IsGoogleUser);
    }

    [UnitFact]
    public async Task GivenAnyRequest_WhenListing_ThenNoNameOrAddressIsExposed()
    {
        // The subject has asked to be erased; a queue an administrator works from is the last place
        // to widen who sees their details. The projection carries no field for either.
        var persons = new AsyncFakeRepository<Person>();
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        await SeedPersonAsync(persons, DateTime.UtcNow, DateTime.UtcNow.AddDays(30));

        var output = await Handler(persons, googleUsers).HandleAsync(Query());
        var result = Assert.Single(output.Data!);
        var properties = result.GetType().GetProperties().Select(property => property.Name).ToList();

        Assert.DoesNotContain("Name", properties);
        Assert.DoesNotContain("Email", properties);
    }
}
