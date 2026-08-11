using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for ListScopeGoogleUsersQueryHandler (UC-27, FR-GO-14/FR-GO-17): the scope's Google
// Users only, paginated and filterable, gated by scope ownership. Covers the main flow, a missing or
// logically deleted scope (AF-27a), a non-owning actor (AF-27b), the FR-GO-17 exclusion and its
// escape hatch, and the name/email filters.
public class ListScopeGoogleUsersQueryHandlerTests
{
    private static Scope Scope(long id, bool isDeleted = false) =>
        new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}", IsDeleted = isDeleted };

    private static GoogleUser GoogleUser(
        long id, Scope scope, string name, string email, bool isDeleted = false) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        GoogleId = $"google-sub-{id}",
        Name = name,
        Email = email,
        EmailVerified = true,
        IsDeleted = isDeleted,
        ScopeId = scope.Id,
        Scope = scope
    };

    private static IScopeOwnershipChecker Ownership(bool allowed)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);

        return checker.Object;
    }

    private static async Task<AsyncFakeRepository<Scope>> ScopesWith(params Scope[] scopes)
    {
        var repository = new AsyncFakeRepository<Scope>();

        foreach (var scope in scopes)
        {
            await repository.CreateAsync(scope);
        }

        return repository;
    }

    private static async Task<AsyncFakeRepository<GoogleUser>> GoogleUsersWith(params GoogleUser[] googleUsers)
    {
        var repository = new AsyncFakeRepository<GoogleUser>();

        foreach (var googleUser in googleUsers)
        {
            await repository.CreateAsync(googleUser);
        }

        return repository;
    }

    private static ListScopeGoogleUsersQuery Query(
        Scope scope,
        Roles actingRole = Roles.SystemAdmin,
        bool includeDeleted = false,
        string? name = null,
        string? email = null) =>
        new()
        {
            ScopeId = scope.PublicId,
            Name = name,
            Email = email,
            IncludeDeleted = includeDeleted,
            ActingPersonId = Guid.NewGuid(),
            ActingRole = (int)actingRole
        };

    [UnitFact]
    public async Task GivenScopeWithGoogleUsers_WhenListing_ThenReturnsOnlyThatScopesActiveOnes()
    {
        // Given two scopes, each with Google Users, one of them logically deleted (FR-GO-06/17)
        var scope = Scope(1);
        var other = Scope(2);
        var scopes = await ScopesWith(scope, other);
        var googleUsers = await GoogleUsersWith(
            GoogleUser(1, scope, "Alice", "alice@gmail.test"),
            GoogleUser(2, scope, "Bob", "bob@gmail.test"),
            GoogleUser(3, scope, "Deleted", "deleted@gmail.test", isDeleted: true),
            GoogleUser(4, other, "Elsewhere", "elsewhere@gmail.test"));
        var handler = new ListScopeGoogleUsersQueryHandler(scopes, googleUsers, Ownership(true));

        // When
        var output = await handler.HandleAsync(Query(scope));

        // Then
        Assert.True(output.Success);
        Assert.Contains(GoogleUserMessages.GoogleUsersRetrievedSuccessfully, output.Messages);
        Assert.Equal(["Alice", "Bob"], output.Data!.Select(x => x.Name));
        Assert.All(output.Data!, googleUser => Assert.Equal(scope.PublicId, googleUser.ScopeId));
    }

    [UnitFact]
    public async Task GivenIncludeDeleted_WhenListing_ThenReturnsDeletedOnesToo()
    {
        // Given the same scope, with the deleted record explicitly requested (FR-GO-17)
        var scope = Scope(1);
        var scopes = await ScopesWith(scope);
        var googleUsers = await GoogleUsersWith(
            GoogleUser(1, scope, "Alice", "alice@gmail.test"),
            GoogleUser(2, scope, "Deleted", "deleted@gmail.test", isDeleted: true));
        var handler = new ListScopeGoogleUsersQueryHandler(scopes, googleUsers, Ownership(true));

        // When
        var output = await handler.HandleAsync(Query(scope, includeDeleted: true));

        // Then
        Assert.True(output.Success);
        Assert.Equal(2, output.Data!.Count());
    }

    [UnitFact]
    public async Task GivenNameFilter_WhenListing_ThenMatchesCaseInsensitiveSubstring()
    {
        // Given FR-GO-14's filtering
        var scope = Scope(1);
        var scopes = await ScopesWith(scope);
        var googleUsers = await GoogleUsersWith(
            GoogleUser(1, scope, "Alice Anderson", "alice@gmail.test"),
            GoogleUser(2, scope, "Bob Brown", "bob@gmail.test"));
        var handler = new ListScopeGoogleUsersQueryHandler(scopes, googleUsers, Ownership(true));

        // When
        var output = await handler.HandleAsync(Query(scope, name: "ANDERSON"));

        // Then
        var only = Assert.Single(output.Data!);
        Assert.Equal("Alice Anderson", only.Name);
    }

    [UnitFact]
    public async Task GivenEmailFilter_WhenListing_ThenMatchesCaseInsensitiveSubstring()
    {
        // Given the same, on the other filter
        var scope = Scope(1);
        var scopes = await ScopesWith(scope);
        var googleUsers = await GoogleUsersWith(
            GoogleUser(1, scope, "Alice", "alice@gmail.test"),
            GoogleUser(2, scope, "Bob", "bob@outlook.test"));
        var handler = new ListScopeGoogleUsersQueryHandler(scopes, googleUsers, Ownership(true));

        // When
        var output = await handler.HandleAsync(Query(scope, email: "GMAIL"));

        // Then
        var only = Assert.Single(output.Data!);
        Assert.Equal("Alice", only.Name);
    }

    [UnitFact]
    public async Task GivenNoSuchScope_WhenListing_ThenReturnsScopeNotFoundError()
    {
        // Given a scope id nobody holds (AF-27a)
        var scope = Scope(1);
        var scopes = await ScopesWith();
        var handler = new ListScopeGoogleUsersQueryHandler(
            scopes, await GoogleUsersWith(), Ownership(true));

        // When
        var output = await handler.HandleAsync(Query(scope));

        // Then
        Assert.False(output.Success);
        Assert.Contains(GoogleUserMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedScope_WhenListing_ThenReturnsScopeNotFoundError()
    {
        // Given a scope that exists but is logically deleted (AF-27a)
        var scope = Scope(1, isDeleted: true);
        var scopes = await ScopesWith(scope);
        var handler = new ListScopeGoogleUsersQueryHandler(
            scopes, await GoogleUsersWith(GoogleUser(1, scope, "Alice", "alice@gmail.test")),
            Ownership(true));

        // When
        var output = await handler.HandleAsync(Query(scope));

        // Then
        Assert.False(output.Success);
        Assert.Contains(GoogleUserMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenNonOwningScopeAdmin_WhenListing_ThenReturnsNotScopeOwnerError()
    {
        // Given a Scope Admin who does not own the scope (AF-27b)
        var scope = Scope(1);
        var scopes = await ScopesWith(scope);
        var handler = new ListScopeGoogleUsersQueryHandler(
            scopes, await GoogleUsersWith(GoogleUser(1, scope, "Alice", "alice@gmail.test")),
            Ownership(false));

        // When
        var output = await handler.HandleAsync(Query(scope, Roles.ScopeAdmin));

        // Then — refused before any Google User is read, so the listing cannot leak a count either
        Assert.False(output.Success);
        Assert.Contains(GoogleUserMessages.NotScopeOwner, output.Errors);
    }

    [UnitFact]
    public async Task GivenScopeWithNoGoogleUsers_WhenListing_ThenReturnsEmptyPage()
    {
        // Given an active scope that nobody has signed into with Google yet
        var scope = Scope(1);
        var scopes = await ScopesWith(scope);
        var handler = new ListScopeGoogleUsersQueryHandler(
            scopes, await GoogleUsersWith(), Ownership(true));

        // When
        var output = await handler.HandleAsync(Query(scope));

        // Then — an empty page is a success, not an AF-27a
        Assert.True(output.Success);
        Assert.Empty(output.Data!);
    }
}
