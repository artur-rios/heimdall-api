using ArturRios.Data.Relational.Core.Entities;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for ListMyScopeInvitationsQueryHandler (UC-59, FR-SC-16): the caller's own open invitations,
// soonest to expire first, with the scope's id and name and the inviter's name; somebody else's, ended,
// expired and a deleted scope's are not listed; the page bound.
public class ListMyScopeInvitationsQueryHandlerTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static Scope Scope(long id, bool isDeleted = false) => new()
    {
        Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}", IsDeleted = isDeleted
    };

    private static Person Person(long id, string name) => new()
    {
        Id = id, PublicId = Guid.NewGuid(), Name = name, Email = $"{name.ToLowerInvariant()}@test.local",
        RoleId = (long)Roles.ScopeAdmin
    };

    private static ScopeOwnerInvitation Invitation(long id, Scope scope, Person invitee, Person? inviter,
        DateTime? expiresAt = null, DateTime? endedAt = null) => new()
    {
        Id = id, PublicId = Guid.NewGuid(),
        ScopeId = scope.Id, Scope = scope,
        InviteePersonId = invitee.Id, Invitee = invitee,
        InvitedByPersonId = inviter?.Id, InvitedBy = inviter,
        CreatedAt = Now.AddDays(-1), ExpiresAt = expiresAt ?? Now.AddDays(6),
        EndedAt = endedAt, Outcome = endedAt is null ? null : ScopeOwnerInvitationOutcomes.Declined
    };

    private static async Task<AsyncFakeRepository<T, long>> RepositoryWith<T>(params T[] items) where T : Entity<long>
    {
        var repository = new AsyncFakeRepository<T, long>();

        foreach (var item in items)
        {
            await repository.CreateAsync(item);
        }

        return repository;
    }

    private static ListMyScopeInvitationsQuery Query(Person actor, int pageSize = 20) => new()
    {
        PageNumber = 1, PageSize = pageSize, ActingPersonId = actor.PublicId, ActingRole = (int)Roles.ScopeAdmin
    };

    [UnitFact]
    public async Task GivenOwnAndOtherInvitations_WhenListing_ThenOnlyOwnOpenOnesAreReturnedSoonestFirst()
    {
        // Given
        var acme = Scope(1);
        var globex = Scope(2);
        var gone = Scope(3, isDeleted: true);
        var ada = Person(1, "Ada");
        var grace = Person(2, "Grace");
        var alan = Person(3, "Alan");
        var later = Invitation(1, acme, grace, ada, expiresAt: Now.AddDays(6));
        var sooner = Invitation(2, globex, grace, ada, expiresAt: Now.AddDays(2));
        var handler = new ListMyScopeInvitationsQueryHandler(
            await RepositoryWith(ada, grace, alan),
            await RepositoryWith(later, sooner,
                Invitation(3, acme, alan, ada),
                Invitation(4, gone, grace, ada),
                Invitation(5, globex, grace, ada, endedAt: Now.AddMinutes(-1)),
                Invitation(6, acme, grace, ada, expiresAt: Now.AddMinutes(-1))),
            new ListMyScopeInvitationsQueryValidator());

        // When
        var output = await handler.HandleAsync(Query(grace));

        // Then
        Assert.True(output.Success);
        Assert.Equal([sooner.PublicId, later.PublicId], output.Data!.Select(x => x.Id));
        var first = output.Data!.First();
        Assert.Equal(globex.PublicId, first.ScopeId);
        Assert.Equal("scope-2", first.ScopeName);
        Assert.Equal("Ada", first.InvitedByName);
        Assert.Equal(sooner.ExpiresAt, first.ExpiresAt);
    }

    [UnitFact]
    public async Task GivenAnInvitationWhoseInviterWasCleared_WhenListing_ThenItIsListedWithNoInviterName()
    {
        // Given
        var acme = Scope(1);
        var grace = Person(1, "Grace");
        var invitation = Invitation(1, acme, grace, null);
        var handler = new ListMyScopeInvitationsQueryHandler(
            await RepositoryWith(grace), await RepositoryWith(invitation), new ListMyScopeInvitationsQueryValidator());

        // When
        var output = await handler.HandleAsync(Query(grace));

        // Then
        var listed = Assert.Single(output.Data!);
        Assert.Equal(invitation.PublicId, listed.Id);
        Assert.Null(listed.InvitedByName);
    }

    [UnitFact]
    public async Task GivenAPageSizeOfZero_WhenListing_ThenInvalidPageSize()
    {
        // Given NFR-10
        var grace = Person(1, "Grace");
        var handler = new ListMyScopeInvitationsQueryHandler(
            await RepositoryWith(grace), await RepositoryWith<ScopeOwnerInvitation>(),
            new ListMyScopeInvitationsQueryValidator());

        // When
        var output = await handler.HandleAsync(Query(grace, pageSize: 0));

        // Then
        Assert.Contains(PaginationMessages.InvalidPageSize, output.Errors);
    }
}
