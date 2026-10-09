using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for PasswordRecoveryProcessor (UC-12 steps 2 to 4, run after the caller has been
// answered). The caller's answer is the same on every path and is the handler's business; what is
// left to test here is whether a token is issued, and for whom. AF-12a is the case where none is.
public class PasswordRecoveryProcessorTests
{
    private static Scope Scope(long id, bool isDeleted = false) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"scope-{id}",
        IsDeleted = isDeleted
    };

    private static Person Person(long id, string email, Roles role, bool isDeleted = false) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"person-{id}",
        Email = email,
        RoleId = (long)role,
        IsDeleted = isDeleted
    };

    private static Person User(long id, string email, Scope scope, bool isDeleted = false)
    {
        var person = Person(id, email, Roles.User, isDeleted);
        person.ScopeId = scope.Id;
        person.ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope };
        return person;
    }

    private static Person ScopeAdmin(long id, string email, params Scope[] owned)
    {
        var person = Person(id, email, Roles.ScopeAdmin);
        person.ScopeOwnerships = owned
            .Select(scope => new ScopeOwner { ScopeId = scope.Id, Scope = scope })
            .ToList();
        return person;
    }

    private static async Task<AsyncFakeRepository<Person, long>> PersonsWith(params Person[] persons)
    {
        var repository = new AsyncFakeRepository<Person, long>();

        foreach (var person in persons)
        {
            await repository.CreateAsync(person);
        }

        return repository;
    }

    /// <summary>
    ///     Records who a token was issued for, so a test can assert on the one thing that separates
    ///     the main flow from AF-12a. A <c>null</c> <see cref="Recipient" /> is the assertion that
    ///     nothing was issued and no email attempted.
    /// </summary>
    private sealed class RecordingResetService : IPasswordResetService
    {
        public Person? Recipient { get; private set; }

        public Task IssueAndSendAsync(Person person)
        {
            Recipient = person;

            return Task.CompletedTask;
        }
    }

    private static PasswordRecoveryProcessor ProcessorFor(
        AsyncFakeRepository<Person, long> persons, IPasswordResetService passwordReset) =>
        new(persons, passwordReset);

    private static PasswordRecoveryRequest Request(string email, Guid? scopeId = null) => new(email, scopeId);

    [UnitFact]
    public async Task GivenUserWithMatchingScope_WhenProcessingRecovery_ThenTokenIsIssuedForThem()
    {
        // Given a User of a live scope
        var scope = Scope(1);
        var person = User(10, "user@test.local", scope);
        var persons = await PersonsWith(person);
        var passwordReset = new RecordingResetService();

        // When
        await ProcessorFor(persons, passwordReset)
            .ProcessAsync(Request("user@test.local", scope.PublicId));

        // Then
        Assert.Same(person, passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenScopeAdminWithoutScopeId_WhenProcessingRecovery_ThenTokenIsIssuedForThem()
    {
        // Given a ScopeAdmin owning a live scope, recovering without naming one
        var person = ScopeAdmin(10, "admin@test.local", Scope(1));
        var persons = await PersonsWith(person);
        var passwordReset = new RecordingResetService();

        // When
        await ProcessorFor(persons, passwordReset).ProcessAsync(Request("admin@test.local"));

        // Then
        Assert.Same(person, passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenSystemAdmin_WhenProcessingRecovery_ThenTokenIsIssuedForThem()
    {
        // Given a SystemAdmin, who belongs to no scope and so has none that could be deleted
        var person = Person(10, "root@test.local", Roles.SystemAdmin);
        var persons = await PersonsWith(person);
        var passwordReset = new RecordingResetService();

        // When
        await ProcessorFor(persons, passwordReset).ProcessAsync(Request("root@test.local"));

        // Then
        Assert.Same(person, passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenEmailInDifferentCase_WhenProcessingRecovery_ThenTokenIsIssuedForThem()
    {
        // Given the stored email differs only in case from the submitted one — the same
        // case-insensitive comparison that governs uniqueness and login
        var person = Person(10, "Admin@Test.Local", Roles.SystemAdmin);
        var persons = await PersonsWith(person);
        var passwordReset = new RecordingResetService();

        // When
        await ProcessorFor(persons, passwordReset).ProcessAsync(Request("admin@test.local"));

        // Then
        Assert.Same(person, passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenUnknownEmail_WhenProcessingRecovery_ThenNoTokenIsIssued()
    {
        // Given — AF-12a: the address belongs to nobody
        var persons = await PersonsWith(Person(10, "admin@test.local", Roles.SystemAdmin));
        var passwordReset = new RecordingResetService();

        // When
        await ProcessorFor(persons, passwordReset).ProcessAsync(Request("nobody@test.local"));

        // Then — nothing issued
        Assert.Null(passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenUserOfAnotherScope_WhenProcessingRecovery_ThenNoTokenIsIssued()
    {
        // Given — AF-12a: the email exists, but as a User of a different scope. Two Users may share
        // an email across scopes, so the scope is part of the identity being recovered.
        var theirScope = Scope(1);
        var otherScope = Scope(2);
        var persons = await PersonsWith(User(10, "user@test.local", theirScope));
        var passwordReset = new RecordingResetService();

        // When
        await ProcessorFor(persons, passwordReset)
            .ProcessAsync(Request("user@test.local", otherScope.PublicId));

        // Then
        Assert.Null(passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenUserEmailWithoutScopeId_WhenProcessingRecovery_ThenNoTokenIsIssued()
    {
        // Given — AF-12a: without a scope id the admin lookup runs, which must not reach a User
        var persons = await PersonsWith(User(10, "user@test.local", Scope(1)));
        var passwordReset = new RecordingResetService();

        // When
        await ProcessorFor(persons, passwordReset).ProcessAsync(Request("user@test.local"));

        // Then
        Assert.Null(passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedPerson_WhenProcessingRecovery_ThenNoTokenIsIssued()
    {
        // Given a deleted account. UC-11 refuses to authenticate it (AF-11c), so a reset link would
        // produce a password that cannot be used — and saying so would confirm the account exists.
        var persons = await PersonsWith(
            Person(10, "admin@test.local", Roles.SystemAdmin, isDeleted: true));
        var passwordReset = new RecordingResetService();

        // When
        await ProcessorFor(persons, passwordReset).ProcessAsync(Request("admin@test.local"));

        // Then
        Assert.Null(passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenUserWhoseScopeIsDeleted_WhenProcessingRecovery_ThenNoTokenIsIssued()
    {
        // Given a live User of a deleted scope — refused at login by AF-11d
        var scope = Scope(1, isDeleted: true);
        var persons = await PersonsWith(User(10, "user@test.local", scope));
        var passwordReset = new RecordingResetService();

        // When
        await ProcessorFor(persons, passwordReset)
            .ProcessAsync(Request("user@test.local", scope.PublicId));

        // Then
        Assert.Null(passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenScopeAdminWhoseScopesAreAllDeleted_WhenProcessingRecovery_ThenNoTokenIsIssued()
    {
        // Given a ScopeAdmin with nothing left to administer — refused at login by AF-11e
        var persons = await PersonsWith(ScopeAdmin(
            10, "admin@test.local", Scope(1, isDeleted: true), Scope(2, isDeleted: true)));
        var passwordReset = new RecordingResetService();

        // When
        await ProcessorFor(persons, passwordReset).ProcessAsync(Request("admin@test.local"));

        // Then
        Assert.Null(passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenScopeAdminWithOneLiveScope_WhenProcessingRecovery_ThenTokenIsIssuedForThem()
    {
        // Given the boundary of the rule above: one owned scope deleted, one still live
        var person = ScopeAdmin(10, "admin@test.local", Scope(1, isDeleted: true), Scope(2));
        var persons = await PersonsWith(person);
        var passwordReset = new RecordingResetService();

        // When
        await ProcessorFor(persons, passwordReset).ProcessAsync(Request("admin@test.local"));

        // Then
        Assert.Same(person, passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenARestrictedPerson_WhenProcessingRecovery_ThenNoTokenIsIssued()
    {
        // Given — NFR-24: mailing a restricted identity would be processing it
        var person = Person(10, "admin@test.local", Roles.SystemAdmin);
        person.ProcessingRestrictedAt = DateTime.UtcNow.AddDays(-1);
        var persons = await PersonsWith(person);
        var passwordReset = new RecordingResetService();

        // When
        await ProcessorFor(persons, passwordReset).ProcessAsync(Request("admin@test.local"));

        // Then
        Assert.Null(passwordReset.Recipient);
    }
}
