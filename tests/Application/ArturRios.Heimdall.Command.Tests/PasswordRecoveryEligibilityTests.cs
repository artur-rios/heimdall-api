using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for PasswordRecoveryEligibility: UC-12's rule for who is worth sending a reset link to,
// now shared with UC-52. It mirrors UC-11's account checks (FR-AU-05/06/07) and NFR-24, and reports
// which one failed so an authenticated caller can be told.
public class PasswordRecoveryEligibilityTests
{
    private static Scope Scope(long id, bool isDeleted = false) =>
        new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}", IsDeleted = isDeleted };

    private static Person Person(Roles role, bool isDeleted = false, bool restricted = false) => new()
    {
        Id = 1,
        PublicId = Guid.NewGuid(),
        Name = "person",
        Email = "person@test.local",
        RoleId = (long)role,
        IsDeleted = isDeleted,
        ProcessingRestrictedAt = restricted ? DateTime.UtcNow : null
    };

    private static Person User(Scope scope, bool isDeleted = false, bool restricted = false)
    {
        var person = Person(Roles.User, isDeleted, restricted);
        person.ScopeId = scope.Id;
        person.ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope };
        return person;
    }

    private static Person ScopeAdmin(params Scope[] owned)
    {
        var person = Person(Roles.ScopeAdmin);
        person.ScopeOwnerships = owned.Select(scope => new ScopeOwner { ScopeId = scope.Id, Scope = scope }).ToList();
        return person;
    }

    [UnitFact]
    public void GivenAUserOfALiveScope_WhenEvaluated_ThenEligible()
    {
        // Given
        var person = User(Scope(1));

        // When
        var eligibility = PasswordRecoveryEligibility.Of(person);

        // Then
        Assert.Equal(RecoveryEligibility.Eligible, eligibility);
    }

    [UnitFact]
    public void GivenASystemAdmin_WhenEvaluated_ThenEligible()
    {
        // Given
        var person = Person(Roles.SystemAdmin);

        // When
        var eligibility = PasswordRecoveryEligibility.Of(person);

        // Then
        Assert.Equal(RecoveryEligibility.Eligible, eligibility);
    }

    [UnitFact]
    public void GivenAScopeAdminWithOneLiveScope_WhenEvaluated_ThenEligible()
    {
        // Given one deleted and one live owned scope — FR-AU-07 refuses only when all are deleted
        var person = ScopeAdmin(Scope(1, isDeleted: true), Scope(2));

        // When
        var eligibility = PasswordRecoveryEligibility.Of(person);

        // Then
        Assert.Equal(RecoveryEligibility.Eligible, eligibility);
    }

    [UnitFact]
    public void GivenARestrictedPerson_WhenEvaluated_ThenRestricted()
    {
        // Given NFR-24 — sending would be processing a restricted identity
        var person = User(Scope(1), restricted: true);

        // When
        var eligibility = PasswordRecoveryEligibility.Of(person);

        // Then
        Assert.Equal(RecoveryEligibility.Restricted, eligibility);
    }

    [UnitFact]
    public void GivenALogicallyDeletedPerson_WhenEvaluated_ThenDeleted()
    {
        // Given
        var person = User(Scope(1), isDeleted: true);

        // When
        var eligibility = PasswordRecoveryEligibility.Of(person);

        // Then
        Assert.Equal(RecoveryEligibility.Deleted, eligibility);
    }

    [UnitFact]
    public void GivenAPersonBothRestrictedAndDeleted_WhenEvaluated_ThenRestrictedWins()
    {
        // Given both — the restriction is checked first, as UC-12 always has
        var person = User(Scope(1), isDeleted: true, restricted: true);

        // When
        var eligibility = PasswordRecoveryEligibility.Of(person);

        // Then
        Assert.Equal(RecoveryEligibility.Restricted, eligibility);
    }

    [UnitFact]
    public void GivenAUserWhoseScopeIsDeleted_WhenEvaluated_ThenCannotSignIn()
    {
        // Given FR-AU-06
        var person = User(Scope(1, isDeleted: true));

        // When
        var eligibility = PasswordRecoveryEligibility.Of(person);

        // Then
        Assert.Equal(RecoveryEligibility.CannotSignIn, eligibility);
    }

    [UnitFact]
    public void GivenAUserWithNoMembership_WhenEvaluated_ThenCannotSignIn()
    {
        // Given a User with no scope at all — the login could not name one
        var person = Person(Roles.User);

        // When
        var eligibility = PasswordRecoveryEligibility.Of(person);

        // Then
        Assert.Equal(RecoveryEligibility.CannotSignIn, eligibility);
    }

    [UnitFact]
    public void GivenAScopeAdminWhoseScopesAreAllDeleted_WhenEvaluated_ThenCannotSignIn()
    {
        // Given FR-AU-07
        var person = ScopeAdmin(Scope(1, isDeleted: true));

        // When
        var eligibility = PasswordRecoveryEligibility.Of(person);

        // Then
        Assert.Equal(RecoveryEligibility.CannotSignIn, eligibility);
    }

    [UnitFact]
    public void GivenAScopeAdminOwningNothing_WhenEvaluated_ThenCannotSignIn()
    {
        // Given no owned scope — the same answer UC-12 has always given
        var person = ScopeAdmin();

        // When
        var eligibility = PasswordRecoveryEligibility.Of(person);

        // Then
        Assert.Equal(RecoveryEligibility.CannotSignIn, eligibility);
    }
}
