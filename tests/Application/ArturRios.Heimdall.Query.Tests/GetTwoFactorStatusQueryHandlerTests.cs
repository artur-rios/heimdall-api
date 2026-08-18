using ArturRios.Data.Relational.Core.Entities;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for GetTwoFactorStatusQueryHandler (UC-36 – UC-40, FR-2F-15): the caller's own
// two-factor state. Covers no configuration (200, all false), a pending setup, an active
// configuration, the unused-recovery-code count, and a caller who is not an eligible person
// (AF-36b's NotEligible — a Google User, or a token naming no live person).
public class GetTwoFactorStatusQueryHandlerTests
{
    private static Person PersonWith(Guid publicId) => new()
    {
        Id = 1,
        PublicId = publicId,
        Name = "Ana",
        Email = "ana@test.local",
        RoleId = (long)Roles.User
    };

    private static async Task<AsyncFakeRepository<T>> RepositoryWith<T>(params T[] items) where T : Entity
    {
        var repository = new AsyncFakeRepository<T>();

        foreach (var item in items)
        {
            await repository.CreateAsync(item);
        }

        return repository;
    }

    private static GetTwoFactorStatusQuery QueryFor(Guid personId) => new()
    {
        ActingPersonId = personId,
        ActingRole = (int)Roles.User
    };

    [UnitFact]
    public async Task GivenNoConfiguration_WhenHandlingGetTwoFactorStatus_ThenReturnsAllFalse()
    {
        // Given a live person who never enabled two-factor authentication
        var personId = Guid.NewGuid();
        var persons = await RepositoryWith(PersonWith(personId));
        var configurations = await RepositoryWith<TwoFactorAuth>();
        var recoveryCodes = await RepositoryWith<TwoFactorRecoveryCode>();
        var handler = new GetTwoFactorStatusQueryHandler(persons, configurations, recoveryCodes);

        // When
        var output = await handler.HandleAsync(QueryFor(personId));

        // Then — the ordinary "never turned it on" state is a success, not a refusal
        Assert.True(output.Success);
        Assert.False(output.Data!.IsActive);
        Assert.False(output.Data.AppEnabled);
        Assert.False(output.Data.EmailEnabled);
        Assert.Equal(0, output.Data.RemainingRecoveryCodes);
        Assert.Contains(TwoFactorMessages.StatusRetrieved, output.Messages);
    }

    [UnitFact]
    public async Task GivenPendingSetup_WhenHandlingGetTwoFactorStatus_ThenReportsMethodsButNotActive()
    {
        // Given UC-36 initiated setup for both methods and UC-37 has not confirmed it
        var personId = Guid.NewGuid();
        var persons = await RepositoryWith(PersonWith(personId));
        var configurations = await RepositoryWith(new TwoFactorAuth
        {
            Id = 1, PersonId = 1, AppEnabled = true, EmailEnabled = true, IsActive = false
        });
        var recoveryCodes = await RepositoryWith<TwoFactorRecoveryCode>();
        var handler = new GetTwoFactorStatusQueryHandler(persons, configurations, recoveryCodes);

        // When
        var output = await handler.HandleAsync(QueryFor(personId));

        // Then — pending is representable as !IsActive with methods set
        Assert.True(output.Success);
        Assert.False(output.Data!.IsActive);
        Assert.True(output.Data.AppEnabled);
        Assert.True(output.Data.EmailEnabled);
    }

    [UnitFact]
    public async Task GivenActiveConfiguration_WhenHandlingGetTwoFactorStatus_ThenReportsItsMethods()
    {
        // Given an active app-only configuration
        var personId = Guid.NewGuid();
        var persons = await RepositoryWith(PersonWith(personId));
        var configurations = await RepositoryWith(new TwoFactorAuth
        {
            Id = 1, PersonId = 1, AppEnabled = true, EmailEnabled = false, IsActive = true
        });
        var recoveryCodes = await RepositoryWith<TwoFactorRecoveryCode>();
        var handler = new GetTwoFactorStatusQueryHandler(persons, configurations, recoveryCodes);

        // When
        var output = await handler.HandleAsync(QueryFor(personId));

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.IsActive);
        Assert.True(output.Data.AppEnabled);
        Assert.False(output.Data.EmailEnabled);
    }

    [UnitFact]
    public async Task GivenUsedAndUnusedRecoveryCodes_WhenHandlingGetTwoFactorStatus_ThenCountsOnlyUnused()
    {
        // Given three codes for this configuration, one consumed, plus one for another configuration
        var personId = Guid.NewGuid();
        var persons = await RepositoryWith(PersonWith(personId));
        var configurations = await RepositoryWith(new TwoFactorAuth
        {
            Id = 1, PersonId = 1, AppEnabled = true, IsActive = true
        });
        var recoveryCodes = await RepositoryWith(
            new TwoFactorRecoveryCode { Id = 1, TwoFactorAuthId = 1, CodeHash = [1], Used = false },
            new TwoFactorRecoveryCode { Id = 2, TwoFactorAuthId = 1, CodeHash = [2], Used = false },
            new TwoFactorRecoveryCode { Id = 3, TwoFactorAuthId = 1, CodeHash = [3], Used = true },
            new TwoFactorRecoveryCode { Id = 4, TwoFactorAuthId = 2, CodeHash = [4], Used = false });
        var handler = new GetTwoFactorStatusQueryHandler(persons, configurations, recoveryCodes);

        // When
        var output = await handler.HandleAsync(QueryFor(personId));

        // Then — two unused, belonging to this configuration only
        Assert.Equal(2, output.Data!.RemainingRecoveryCodes);
    }

    [UnitFact]
    public async Task GivenCallerIsNotAPerson_WhenHandlingGetTwoFactorStatus_ThenReturnsNotEligible()
    {
        // Given a token naming no live Person — a Google User (separate PublicId space), or a
        // person since removed. AF-36b treats both alike.
        var persons = await RepositoryWith(PersonWith(Guid.NewGuid()));
        var configurations = await RepositoryWith<TwoFactorAuth>();
        var recoveryCodes = await RepositoryWith<TwoFactorRecoveryCode>();
        var handler = new GetTwoFactorStatusQueryHandler(persons, configurations, recoveryCodes);

        // When
        var output = await handler.HandleAsync(QueryFor(Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Null(output.Data);
        Assert.Contains(TwoFactorMessages.NotEligible, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedPerson_WhenHandlingGetTwoFactorStatus_ThenReturnsNotEligible()
    {
        // Given a logically deleted person — the lookup excludes them, as every actor lookup does
        var personId = Guid.NewGuid();
        var person = PersonWith(personId);
        person.IsDeleted = true;
        var persons = await RepositoryWith(person);
        var configurations = await RepositoryWith<TwoFactorAuth>();
        var recoveryCodes = await RepositoryWith<TwoFactorRecoveryCode>();
        var handler = new GetTwoFactorStatusQueryHandler(persons, configurations, recoveryCodes);

        // When
        var output = await handler.HandleAsync(QueryFor(personId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.NotEligible, output.Errors);
    }
}
