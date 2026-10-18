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

// Unit tests for ListScopeOwnerInvitationsQueryHandler (UC-58, FR-SC-15): a scope's open invitations only,
// newest first, with the invitee's name and address and the inviter's name; another scope's, an ended, an
// expired and a deleted invitee's are not listed; AF-58a, AF-58b and the page bound.
public class ListScopeOwnerInvitationsQueryHandlerTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static Scope Scope(long id, bool isDeleted = false) => new()
    {
        Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}", IsDeleted = isDeleted
    };

    private static Person Person(long id, string name, bool isDeleted = false) => new()
    {
        Id = id, PublicId = Guid.NewGuid(), Name = name, Email = $"{name.ToLowerInvariant()}@test.local",
        RoleId = (long)Roles.ScopeAdmin, IsDeleted = isDeleted
    };

    private static ScopeOwnerInvitation Invitation(
        long id, Scope scope, Person invitee, Person? inviter, DateTime? createdAt = null,
        DateTime? expiresAt = null, DateTime? endedAt = null) => new()
    {
        Id = id, PublicId = Guid.NewGuid(),
        ScopeId = scope.Id, Scope = scope,
        InviteePersonId = invitee.Id, Invitee = invitee,
        InvitedByPersonId = inviter?.Id, InvitedBy = inviter,
        CreatedAt = createdAt ?? Now.AddMinutes(-id), ExpiresAt = expiresAt ?? Now.AddDays(7),
        EndedAt = endedAt, Outcome = endedAt is null ? null : ScopeOwnerInvitationOutcomes.Withdrawn
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

    private static IScopeOwnershipChecker Ownership(bool allowed)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);
        return checker.Object;
    }

    private static ListScopeOwnerInvitationsQueryHandler Handler(
        AsyncFakeRepository<Scope, long> scopes, AsyncFakeRepository<ScopeOwnerInvitation, long> invitations,
        bool allowed = true) =>
        new(scopes, invitations, Ownership(allowed), new ListScopeOwnerInvitationsQueryValidator());

    private static ListScopeOwnerInvitationsQuery Query(Scope scope, int pageSize = 20) => new()
    {
        ScopeId = scope.PublicId, PageNumber = 1, PageSize = pageSize,
        ActingPersonId = Guid.NewGuid(), ActingRole = (int)Roles.ScopeAdmin
    };

    [UnitFact]
    public async Task GivenOpenEndedAndExpiredInvitations_WhenListing_ThenOnlyTheOpenOnesAreReturnedNewestFirst()
    {
        // Given
        var scope = Scope(1);
        var ada = Person(1, "Ada");
        var grace = Person(2, "Grace");
        var alan = Person(3, "Alan");
        var hedy = Person(4, "Hedy");
        var older = Invitation(1, scope, grace, ada, createdAt: Now.AddMinutes(-20));
        var newer = Invitation(2, scope, alan, ada, createdAt: Now.AddMinutes(-10));
        var ended = Invitation(3, scope, hedy, ada, endedAt: Now.AddMinutes(-1));
        var handler = Handler(await RepositoryWith(scope), await RepositoryWith(older, newer, ended,
            Invitation(4, scope, Person(5, "Expired"), ada, createdAt: Now.AddDays(-8), expiresAt: Now.AddDays(-1))));

        // When
        var output = await handler.HandleAsync(Query(scope));

        // Then
        Assert.True(output.Success);
        Assert.Equal(2, output.TotalItems);
        Assert.Equal([newer.PublicId, older.PublicId], output.Data!.Select(x => x.Id));
        var first = output.Data!.First();
        Assert.Equal(alan.PublicId, first.InviteeId);
        Assert.Equal("Alan", first.InviteeName);
        Assert.Equal("alan@test.local", first.InviteeEmail);
        Assert.Equal("Ada", first.InvitedByName);
        Assert.Equal(newer.ExpiresAt, first.ExpiresAt);
    }

    [UnitFact]
    public async Task GivenAnotherScopesInvitationAndADeletedInvitee_WhenListing_ThenNeitherIsReturned()
    {
        // Given
        var scope = Scope(1);
        var other = Scope(2);
        var ada = Person(1, "Ada");
        var handler = Handler(await RepositoryWith(scope, other), await RepositoryWith(
            Invitation(1, other, Person(2, "Grace"), ada),
            Invitation(2, scope, Person(3, "Gone", isDeleted: true), ada)));

        // When
        var output = await handler.HandleAsync(Query(scope));

        // Then
        Assert.True(output.Success);
        Assert.Empty(output.Data!);
    }

    [UnitFact]
    public async Task GivenAnInvitationWhoseInviterWasCleared_WhenListing_ThenItIsListedWithoutAnInviter()
    {
        // Given — Review Focus 4
        var scope = Scope(1);
        var invitation = Invitation(1, scope, Person(1, "Grace"), null);
        var handler = Handler(await RepositoryWith(scope), await RepositoryWith(invitation));

        // When
        var output = await handler.HandleAsync(Query(scope));

        // Then
        Assert.Null(Assert.Single(output.Data!).InvitedByName);
    }

    [UnitFact]
    public async Task GivenACallerWhoMayNotManageTheScope_WhenListing_ThenNotScopeOwner()
    {
        // Given AF-58b
        var scope = Scope(1);
        var handler = Handler(await RepositoryWith(scope), await RepositoryWith<ScopeOwnerInvitation>(), allowed: false);

        // When
        var output = await handler.HandleAsync(Query(scope));

        // Then
        Assert.Equal([ScopeMessages.NotScopeOwner], output.Errors);
    }

    [UnitFact]
    public async Task GivenALogicallyDeletedScope_WhenListing_ThenScopeNotFound()
    {
        // Given AF-58a
        var scope = Scope(1, isDeleted: true);
        var handler = Handler(await RepositoryWith(scope), await RepositoryWith<ScopeOwnerInvitation>());

        // When
        var output = await handler.HandleAsync(Query(scope));

        // Then
        Assert.Equal([ScopeMessages.ScopeNotFound], output.Errors);
    }

    [UnitFact]
    public async Task GivenAPageSizeOfZero_WhenListing_ThenInvalidPageSize()
    {
        // Given NFR-10
        var scope = Scope(1);
        var handler = Handler(await RepositoryWith(scope), await RepositoryWith<ScopeOwnerInvitation>());

        // When
        var output = await handler.HandleAsync(Query(scope, pageSize: 0));

        // Then
        Assert.Contains(PaginationMessages.InvalidPageSize, output.Errors);
    }
}
