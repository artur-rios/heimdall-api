using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for PasswordRecoveryCommandHandler (UC-12). The interesting property is not what the
// handler returns — every path returns the same success — but what it does or does not do on the
// way there. So each test asserts twice: that the response is the one generic message, and whether
// a token was issued. AF-12a is the case where the two come apart.
public class PasswordRecoveryCommandHandlerTests
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

    private static async Task<AsyncFakeRepository<Person>> PersonsWith(params Person[] persons)
    {
        var repository = new AsyncFakeRepository<Person>();

        foreach (var person in persons)
        {
            await repository.CreateAsync(person);
        }

        return repository;
    }

    private static Mock<IValidator<PasswordRecoveryCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<PasswordRecoveryCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<PasswordRecoveryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator;
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

    private static PasswordRecoveryCommandHandler HandlerFor(
        AsyncFakeRepository<Person> persons, IPasswordResetService passwordReset) =>
        new(ValidValidator().Object, persons, passwordReset);

    private static PasswordRecoveryCommand Command(string email, Guid? scopeId = null) =>
        new() { Email = email, ScopeId = scopeId };

    /// <summary>The response every path must produce, asserted identically everywhere.</summary>
    private static void AssertGenericSuccess(ArturRios.Output.DataOutput<Command.Output.PasswordRecoveryCommandOutput?> output)
    {
        Assert.True(output.Success);
        Assert.Empty(output.Errors);
        Assert.Contains(AuthMessages.PasswordRecoveryRequested, output.Messages);
    }

    [UnitFact]
    public async Task GivenUserWithMatchingScope_WhenHandlingPasswordRecovery_ThenTokenIsIssuedForThem()
    {
        // Given a User of a live scope
        var scope = Scope(1);
        var person = User(10, "user@test.local", scope);
        var persons = await PersonsWith(person);
        var passwordReset = new RecordingResetService();

        // When
        var output = await HandlerFor(persons, passwordReset)
            .HandleAsync(Command("user@test.local", scope.PublicId));

        // Then
        AssertGenericSuccess(output);
        Assert.Same(person, passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenScopeAdminWithoutScopeId_WhenHandlingPasswordRecovery_ThenTokenIsIssuedForThem()
    {
        // Given a ScopeAdmin owning a live scope, recovering without naming one
        var person = ScopeAdmin(10, "admin@test.local", Scope(1));
        var persons = await PersonsWith(person);
        var passwordReset = new RecordingResetService();

        // When
        var output = await HandlerFor(persons, passwordReset).HandleAsync(Command("admin@test.local"));

        // Then
        AssertGenericSuccess(output);
        Assert.Same(person, passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenSystemAdmin_WhenHandlingPasswordRecovery_ThenTokenIsIssuedForThem()
    {
        // Given a SystemAdmin, who belongs to no scope and so has none that could be deleted
        var person = Person(10, "root@test.local", Roles.SystemAdmin);
        var persons = await PersonsWith(person);
        var passwordReset = new RecordingResetService();

        // When
        var output = await HandlerFor(persons, passwordReset).HandleAsync(Command("root@test.local"));

        // Then
        AssertGenericSuccess(output);
        Assert.Same(person, passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenEmailInDifferentCase_WhenHandlingPasswordRecovery_ThenTokenIsIssuedForThem()
    {
        // Given the stored email differs only in case from the submitted one — the same
        // case-insensitive comparison that governs uniqueness and login
        var person = Person(10, "Admin@Test.Local", Roles.SystemAdmin);
        var persons = await PersonsWith(person);
        var passwordReset = new RecordingResetService();

        // When
        var output = await HandlerFor(persons, passwordReset).HandleAsync(Command("admin@test.local"));

        // Then
        AssertGenericSuccess(output);
        Assert.Same(person, passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenUnknownEmail_WhenHandlingPasswordRecovery_ThenNoTokenIsIssuedAndAnswerIsUnchanged()
    {
        // Given — AF-12a: the address belongs to nobody
        var persons = await PersonsWith(Person(10, "admin@test.local", Roles.SystemAdmin));
        var passwordReset = new RecordingResetService();

        // When
        var output = await HandlerFor(persons, passwordReset).HandleAsync(Command("nobody@test.local"));

        // Then — the same success a real address gets, and nothing issued behind it
        AssertGenericSuccess(output);
        Assert.Null(passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenUserOfAnotherScope_WhenHandlingPasswordRecovery_ThenNoTokenIsIssued()
    {
        // Given — AF-12a: the email exists, but as a User of a different scope. Two Users may share
        // an email across scopes, so the scope is part of the identity being recovered.
        var theirScope = Scope(1);
        var otherScope = Scope(2);
        var persons = await PersonsWith(User(10, "user@test.local", theirScope));
        var passwordReset = new RecordingResetService();

        // When
        var output = await HandlerFor(persons, passwordReset)
            .HandleAsync(Command("user@test.local", otherScope.PublicId));

        // Then
        AssertGenericSuccess(output);
        Assert.Null(passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenUserEmailWithoutScopeId_WhenHandlingPasswordRecovery_ThenNoTokenIsIssued()
    {
        // Given — AF-12a: without a scope id the admin lookup runs, which must not reach a User
        var persons = await PersonsWith(User(10, "user@test.local", Scope(1)));
        var passwordReset = new RecordingResetService();

        // When
        var output = await HandlerFor(persons, passwordReset).HandleAsync(Command("user@test.local"));

        // Then
        AssertGenericSuccess(output);
        Assert.Null(passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedPerson_WhenHandlingPasswordRecovery_ThenNoTokenIsIssued()
    {
        // Given a deleted account. UC-11 refuses to authenticate it (AF-11c), so a reset link would
        // produce a password that cannot be used — and saying so would confirm the account exists.
        var persons = await PersonsWith(
            Person(10, "admin@test.local", Roles.SystemAdmin, isDeleted: true));
        var passwordReset = new RecordingResetService();

        // When
        var output = await HandlerFor(persons, passwordReset).HandleAsync(Command("admin@test.local"));

        // Then
        AssertGenericSuccess(output);
        Assert.Null(passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenUserWhoseScopeIsDeleted_WhenHandlingPasswordRecovery_ThenNoTokenIsIssued()
    {
        // Given a live User of a deleted scope — refused at login by AF-11d
        var scope = Scope(1, isDeleted: true);
        var persons = await PersonsWith(User(10, "user@test.local", scope));
        var passwordReset = new RecordingResetService();

        // When
        var output = await HandlerFor(persons, passwordReset)
            .HandleAsync(Command("user@test.local", scope.PublicId));

        // Then
        AssertGenericSuccess(output);
        Assert.Null(passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenScopeAdminWhoseScopesAreAllDeleted_WhenHandlingPasswordRecovery_ThenNoTokenIsIssued()
    {
        // Given a ScopeAdmin with nothing left to administer — refused at login by AF-11e
        var persons = await PersonsWith(ScopeAdmin(
            10, "admin@test.local", Scope(1, isDeleted: true), Scope(2, isDeleted: true)));
        var passwordReset = new RecordingResetService();

        // When
        var output = await HandlerFor(persons, passwordReset).HandleAsync(Command("admin@test.local"));

        // Then
        AssertGenericSuccess(output);
        Assert.Null(passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenScopeAdminWithOneLiveScope_WhenHandlingPasswordRecovery_ThenTokenIsIssuedForThem()
    {
        // Given the boundary of the rule above: one owned scope deleted, one still live
        var person = ScopeAdmin(10, "admin@test.local", Scope(1, isDeleted: true), Scope(2));
        var persons = await PersonsWith(person);
        var passwordReset = new RecordingResetService();

        // When
        var output = await HandlerFor(persons, passwordReset).HandleAsync(Command("admin@test.local"));

        // Then
        AssertGenericSuccess(output);
        Assert.Same(person, passwordReset.Recipient);
    }

    [UnitFact]
    public async Task GivenInvalidInput_WhenHandlingPasswordRecovery_ThenReturnsValidationErrorAndIssuesNoToken()
    {
        // Given the validator rejects the command (NFR-10) — the one answer this endpoint gives that
        // is not the generic success, and the only one that stops the lookup happening at all
        var persons = await PersonsWith(Person(10, "admin@test.local", Roles.SystemAdmin));
        var passwordReset = new RecordingResetService();
        var validator = new Mock<IValidator<PasswordRecoveryCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<PasswordRecoveryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([
                new ValidationFailure(nameof(PasswordRecoveryCommand.Email), AuthMessages.EmailInvalid)
            ]));
        var handler = new PasswordRecoveryCommandHandler(validator.Object, persons, passwordReset);

        // When
        var output = await handler.HandleAsync(Command("not-an-email"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.EmailInvalid, output.Errors);
        Assert.DoesNotContain(AuthMessages.PasswordRecoveryRequested, output.Messages);
        Assert.Null(passwordReset.Recipient);
    }
}
