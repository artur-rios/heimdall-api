using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for AddScopeOwnerCommandHandler (UC-21): a System Admin actor's main flow inserting a
// SCOPE_OWNER row; a Scope Admin actor's opening an invitation instead (FR-SC-14) — the notice, its
// cooldown and its restriction rule, the idempotent re-invite, an expired invitation replaced, and the
// audit entry; AF-21a (scope missing or logically deleted), AF-21b (person missing, deleted, or not a
// ScopeAdmin) — for a Scope Admin actor too, who must be refused before any invitation or notice —
// AF-21c delegation (the checker rejects the actor), and AF-21d (already an owner) for both actors. The AF-21c ownership rule itself is covered by ScopeOwnershipCheckerTests; the
// 401/403-by-attribute flows are covered by PersonControllerAddScopeOwnerTests.
public class AddScopeOwnerCommandHandlerTests
{
    private static IScopeOwnershipChecker OwnershipChecker(bool allowed = true)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(c => c.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);
        return checker.Object;
    }

    private static async Task<Scope> SeedScopeAsync(
        AsyncFakeRepository<Scope, long> scopes, string name = "Acme", bool isDeleted = false)
    {
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = name, IsDeleted = isDeleted };
        await scopes.CreateAsync(scope);
        return scope;
    }

    private static async Task<Person> SeedPersonAsync(
        AsyncFakeRepository<Person, long> persons,
        Roles role = Roles.ScopeAdmin,
        bool isDeleted = false,
        params Scope[] ownedScopes)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Admin",
            Email = $"admin-{Guid.NewGuid():N}@test.local",
            RoleId = (long)role,
            IsDeleted = isDeleted,
            ScopeOwnerships = [.. ownedScopes.Select(scope => new ScopeOwner { ScopeId = scope.Id })]
        };

        await persons.CreateAsync(person);
        return person;
    }

    private static AddScopeOwnerCommand Command(
        Guid scopeId, Guid personId, Roles actingRole = Roles.SystemAdmin, Guid? actingPersonId = null) => new()
    {
        ScopeId = scopeId,
        PersonId = personId,
        ActingRole = (int)actingRole,
        ActingPersonId = actingPersonId ?? Guid.NewGuid()
    };

    private static AddScopeOwnerCommandHandler Handler(
        AsyncFakeRepository<Scope, long> scopes,
        AsyncFakeRepository<Person, long> persons,
        bool allowed = true,
        IAtomicWrites? atomicWrites = null,
        AsyncFakeRepository<ScopeOwnerInvitation, long>? invitations = null,
        RecordingScopeInvitationNoticeSender? notices = null,
        string? cooldownSeconds = null,
        IAuditLogWriter? audit = null)
    {
        invitations ??= new AsyncFakeRepository<ScopeOwnerInvitation, long>();

        return new(scopes, persons, OwnershipChecker(allowed), atomicWrites ?? InMemoryAtomicWrites.Instance,
            invitations,
            new InMemoryScopeOwnerInvitations(invitations, persons, scopes),
            new ScopeInvitationOptions(),
            AdminEmailOptions.From(cooldownSeconds),
            notices ?? new RecordingScopeInvitationNoticeSender(),
            audit ?? new RecordingAuditLogWriter(),
            NullLogger<AddScopeOwnerCommandHandler>.Instance);
    }

    private static async Task<ScopeOwnerInvitation> SeedInvitationAsync(
        AsyncFakeRepository<ScopeOwnerInvitation, long> invitations, Scope scope, Person invitee,
        DateTime createdAt, DateTime expiresAt, DateTime? endedAt = null, ScopeOwnerInvitationOutcomes? outcome = null)
    {
        var invitation = new ScopeOwnerInvitation
        {
            PublicId = Guid.NewGuid(), ScopeId = scope.Id, InviteePersonId = invitee.Id,
            CreatedAt = createdAt, ExpiresAt = expiresAt, EndedAt = endedAt, Outcome = outcome
        };
        await invitations.CreateAsync(invitation);
        return invitation;
    }

    private static async Task<List<ScopeOwnerInvitation>> AllAsync(AsyncFakeRepository<ScopeOwnerInvitation, long> invitations) =>
        (await invitations.GetAllAsync()).Data!.ToList();

    private static async Task<Person> StoredAsync(AsyncFakeRepository<Person, long> persons, Person person) =>
        (await persons.GetAllAsync()).Data!.Single(x => x.PublicId == person.PublicId);

    [UnitFact]
    public async Task GivenSystemAdminAndScopeAdminPerson_WhenHandlingAddScopeOwner_ThenOwnershipIsAdded()
    {
        // Given a scope and a ScopeAdmin who does not own it yet (UC-21 main flow)
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then — output
        Assert.True(output.Success);
        Assert.Equal(scope.PublicId, output.Data!.ScopeId);
        Assert.Equal(person.PublicId, output.Data.PersonId);
        Assert.False(output.Data.AlreadyOwner);
        Assert.Contains(PersonMessages.ScopeOwnerAddedSuccessfully, output.Messages);

        // Then — the SCOPE_OWNER row exists
        var stored = await StoredAsync(persons, person);
        Assert.Equal(scope.Id, Assert.Single(stored.ScopeOwnerships).ScopeId);
    }

    [UnitFact]
    public async Task GivenPersonOwningAnotherScope_WhenHandlingAddScopeOwner_ThenExistingOwnershipsSurvive()
    {
        // Given a ScopeAdmin who already owns a different scope — FR-SC-08 lets a person own several
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var scope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes, "Globex");
        var person = await SeedPersonAsync(persons, ownedScopes: otherScope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then — both ownerships are present
        Assert.True(output.Success);
        var stored = await StoredAsync(persons, person);
        Assert.Equal(2, stored.ScopeOwnerships.Count);
        Assert.Contains(stored.ScopeOwnerships, ownership => ownership.ScopeId == scope.Id);
        Assert.Contains(stored.ScopeOwnerships, ownership => ownership.ScopeId == otherScope.Id);
    }

    [UnitFact]
    public async Task GivenOutput_WhenHandlingAddScopeOwner_ThenItCarriesPublicIdentifiersOnly()
    {
        // Given internal Ids that differ from the public ones, so a leak would be visible
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then — only PublicIds are reported (SRD §4.0)
        Assert.Equal(scope.PublicId, output.Data!.ScopeId);
        Assert.Equal(person.PublicId, output.Data.PersonId);
        Assert.NotEqual(Guid.Empty, output.Data.ScopeId);
        Assert.NotEqual(Guid.Empty, output.Data.PersonId);
    }

    [UnitFact]
    public async Task GivenUnknownScope_WhenHandlingAddScopeOwner_ThenScopeNotFoundIsReported()
    {
        // Given no scope with the requested id (AF-21a)
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var person = await SeedPersonAsync(persons);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(Guid.NewGuid(), person.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
        Assert.Empty((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedScope_WhenHandlingAddScopeOwner_ThenScopeNotFoundIsReported()
    {
        // Given a logically deleted scope — AF-21a treats it as absent
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var scope = await SeedScopeAsync(scopes, isDeleted: true);
        var person = await SeedPersonAsync(persons);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
        Assert.Empty((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenUnknownPerson_WhenHandlingAddScopeOwner_ThenPersonNotValidScopeAdminIsReported()
    {
        // Given no person with the requested id (AF-21b)
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var scope = await SeedScopeAsync(scopes);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotValidScopeAdmin, output.Errors);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedPerson_WhenHandlingAddScopeOwner_ThenPersonNotValidScopeAdminIsReported()
    {
        // Given a logically deleted ScopeAdmin — they can no longer authenticate (AF-21b)
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons, isDeleted: true);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotValidScopeAdmin, output.Errors);
        Assert.Empty((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenPersonWithUserRole_WhenHandlingAddScopeOwner_ThenPersonNotValidScopeAdminIsReported()
    {
        // Given a User — only an administrator may own a scope (FR-SC-08, AF-21b)
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons, Roles.User);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotValidScopeAdmin, output.Errors);
        Assert.Empty((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenSystemAdminPerson_WhenHandlingAddScopeOwner_ThenOwnershipIsAdded()
    {
        // Given a live SystemAdmin — an administrator of either kind may own a scope (FR-SC-08)
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons, Roles.SystemAdmin);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.False(output.Data!.AlreadyOwner);
        Assert.Equal(scope.Id, Assert.Single((await StoredAsync(persons, person)).ScopeOwnerships).ScopeId);
    }

    [UnitFact]
    public async Task GivenScopeAdminNotOwningTheScope_WhenHandlingAddScopeOwner_ThenNotScopeOwnerIsReported()
    {
        // Given the ownership checker rejects the actor (AF-21c)
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons);
        var handler = Handler(scopes, persons, allowed: false);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotScopeOwner, output.Errors);
        Assert.Empty((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenPersonAlreadyOwner_WhenHandlingAddScopeOwner_ThenAlreadyOwnerIsReportedAndNoRowIsAdded()
    {
        // Given a ScopeAdmin who already owns the scope (AF-21d)
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons, ownedScopes: scope);
        var handler = Handler(scopes, persons);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then — success, flagged as the no-op, and still exactly one ownership row
        Assert.True(output.Success);
        Assert.True(output.Data!.AlreadyOwner);
        Assert.Contains(PersonMessages.AlreadyScopeOwner, output.Messages);
        Assert.DoesNotContain(PersonMessages.ScopeOwnerAddedSuccessfully, output.Messages);
        Assert.Equal(scope.Id, Assert.Single((await StoredAsync(persons, person)).ScopeOwnerships).ScopeId);
    }

    [UnitFact]
    public async Task GivenUnauthorizedActorAndUnknownPerson_WhenHandlingAddScopeOwner_ThenNotScopeOwnerIsReported()
    {
        // Given an actor the checker rejects, naming a person who does not exist. The ownership check
        // runs first (design Decision 2), so the refusal must not reveal anything about the person.
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var scope = await SeedScopeAsync(scopes);
        var handler = Handler(scopes, persons, allowed: false);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, Guid.NewGuid(), Roles.ScopeAdmin));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotScopeOwner, output.Errors);
        Assert.DoesNotContain(PersonMessages.PersonNotValidScopeAdmin, output.Errors);
    }

    [UnitFact]
    public async Task GivenThePersonDeletedSinceTheRead_WhenHandlingAddScopeOwner_ThenNoOwnershipIsAdded()
    {
        // Given a deletion another request makes between this one's checks and its write
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons);
        var atomicWrites = new InMemoryAtomicWrites { BeforeConditionalWrite = row => row.IsDeleted = true };

        // When
        var output = await Handler(scopes, persons, atomicWrites: atomicWrites)
            .HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then it is refused as AF-21b, and no ownership was granted to the deleted person
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotValidScopeAdmin, output.Errors);
        Assert.Empty((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenAScopeAdminActor_WhenAddingAnAdministrator_ThenAnInvitationOpensAndNoOwnershipIsWritten()
    {
        // Given an owner of the scope adding a ScopeAdmin who does not own it (FR-SC-14, TH-41)
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var invitations = new AsyncFakeRepository<ScopeOwnerInvitation, long>();
        var scope = await SeedScopeAsync(scopes);
        var actor = await SeedPersonAsync(persons, ownedScopes: scope);
        var person = await SeedPersonAsync(persons);
        var handler = Handler(scopes, persons, invitations: invitations);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin, actor.PublicId));

        // Then — the answer says it was an invitation
        Assert.True(output.Success);
        Assert.Contains(ScopeInvitationMessages.InvitationSent, output.Messages);
        Assert.True(output.Data!.Invited);
        Assert.False(output.Data.AlreadyOwner);

        // Then — one open invitation from the actor, seven days long, and no ownership
        var invitation = Assert.Single(await AllAsync(invitations));
        Assert.Equal(output.Data.InvitationId, invitation.PublicId);
        Assert.Equal(output.Data.InvitationExpiresAt, invitation.ExpiresAt);
        Assert.Equal(actor.Id, invitation.InvitedByPersonId);
        Assert.Equal(person.Id, invitation.InviteePersonId);
        Assert.Null(invitation.EndedAt);
        Assert.Equal(TimeSpan.FromDays(7), invitation.ExpiresAt - invitation.CreatedAt);
        Assert.Empty((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenAScopeAdminActor_WhenInviting_ThenTheInviteeIsToldOnceAndItIsAudited()
    {
        // Given
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var notices = new RecordingScopeInvitationNoticeSender();
        var audit = new RecordingAuditLogWriter();
        var scope = await SeedScopeAsync(scopes);
        var actor = await SeedPersonAsync(persons, ownedScopes: scope);
        var person = await SeedPersonAsync(persons);
        var handler = Handler(scopes, persons, notices: notices, audit: audit);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin, actor.PublicId));

        // Then — the notice
        var notice = Assert.Single(notices.Invitations);
        Assert.Equal(person.Email, notice.To);
        Assert.Equal(actor.Name, notice.InviterName);
        Assert.Equal(scope.Name, notice.ScopeName);
        Assert.Equal(output.Data!.InvitationExpiresAt, notice.ExpiresAt);

        // Then — the creation, with the invitation as target (NFR-09)
        Assert.Equal([(ScopeOwnerInvitationAuditActions.Created, output.Data.InvitationId, true)], audit.Entries);
    }

    [UnitFact]
    public async Task GivenAnOpenInvitation_WhenInvitedAgain_ThenItIsReturnedAndNothingIsSentOrAudited()
    {
        // Given — the spec's idempotent re-invite
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var invitations = new AsyncFakeRepository<ScopeOwnerInvitation, long>();
        var notices = new RecordingScopeInvitationNoticeSender();
        var audit = new RecordingAuditLogWriter();
        var scope = await SeedScopeAsync(scopes);
        var actor = await SeedPersonAsync(persons, ownedScopes: scope);
        var person = await SeedPersonAsync(persons);
        var handler = Handler(scopes, persons, invitations: invitations, notices: notices, audit: audit);
        var first = await handler.HandleAsync(Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin, actor.PublicId));

        // When
        var second = await handler.HandleAsync(Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin, actor.PublicId));

        // Then — the first request's notice and audit entry, and nothing more
        Assert.True(second.Success);
        Assert.Contains(ScopeInvitationMessages.InvitationSent, second.Messages);
        Assert.True(second.Data!.Invited);
        Assert.Equal(first.Data!.InvitationId, second.Data.InvitationId);
        Assert.Single(await AllAsync(invitations));
        Assert.Single(notices.Invitations);
        Assert.Single(audit.Entries);
    }

    [UnitFact]
    public async Task GivenAnInvitationThatExpiredUnanswered_WhenInvitedAgain_ThenANewOneOpensAndTheOldOneEndsAtItsExpiry()
    {
        // Given — Review Focus 2
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var invitations = new AsyncFakeRepository<ScopeOwnerInvitation, long>();
        var notices = new RecordingScopeInvitationNoticeSender();
        var scope = await SeedScopeAsync(scopes);
        var actor = await SeedPersonAsync(persons, ownedScopes: scope);
        var person = await SeedPersonAsync(persons);
        var now = DateTime.UtcNow;
        var expired = await SeedInvitationAsync(invitations, scope, person, now.AddDays(-8), now.AddDays(-1));
        var handler = Handler(scopes, persons, invitations: invitations, notices: notices);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin, actor.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.NotEqual(expired.PublicId, output.Data!.InvitationId);
        Assert.Equal(expired.ExpiresAt, expired.EndedAt);
        Assert.Null(expired.Outcome);
        Assert.Equal(2, (await AllAsync(invitations)).Count);
        Assert.Single(notices.Invitations);
    }

    [UnitFact]
    public async Task GivenAWithdrawnInvitationInsideTheCooldown_WhenInvitedAgain_ThenItOpensButNoSecondNoticeIsSent()
    {
        // Given — Review Focus 1: withdraw-and-re-invite must not flood the invitee's inbox
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var invitations = new AsyncFakeRepository<ScopeOwnerInvitation, long>();
        var notices = new RecordingScopeInvitationNoticeSender();
        var scope = await SeedScopeAsync(scopes);
        var actor = await SeedPersonAsync(persons, ownedScopes: scope);
        var person = await SeedPersonAsync(persons);
        var now = DateTime.UtcNow;
        await SeedInvitationAsync(invitations, scope, person, now.AddMinutes(-1), now.AddDays(7), now,
            ScopeOwnerInvitationOutcomes.Withdrawn);
        var handler = Handler(scopes, persons, invitations: invitations, notices: notices);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin, actor.PublicId));

        // Then
        Assert.True(output.Data!.Invited);
        Assert.Equal(2, (await AllAsync(invitations)).Count);
        Assert.Empty(notices.Invitations);
    }

    [UnitFact]
    public async Task GivenTheCooldownSwitchedOff_WhenInvitedAgainAfterAWithdrawal_ThenTheNoticeIsSent()
    {
        // Given HEIMDALL_ADMIN_EMAIL_COOLDOWN_IN_SECONDS=0
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var invitations = new AsyncFakeRepository<ScopeOwnerInvitation, long>();
        var notices = new RecordingScopeInvitationNoticeSender();
        var scope = await SeedScopeAsync(scopes);
        var actor = await SeedPersonAsync(persons, ownedScopes: scope);
        var person = await SeedPersonAsync(persons);
        var now = DateTime.UtcNow;
        await SeedInvitationAsync(invitations, scope, person, now.AddMinutes(-1), now.AddDays(7), now,
            ScopeOwnerInvitationOutcomes.Withdrawn);
        var handler = Handler(scopes, persons, invitations: invitations, notices: notices, cooldownSeconds: "0");

        // When
        await handler.HandleAsync(Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin, actor.PublicId));

        // Then
        Assert.Single(notices.Invitations);
    }

    [UnitFact]
    public async Task GivenAProcessingRestrictedInvitee_WhenInvited_ThenTheInvitationOpensButNoNoticeIsSent()
    {
        // Given an invitee whose processing is restricted — the admin-mail rule mails them nothing (AF-51c,
        // NFR-24); acceptance would void the invitation, so it opens as for anyone else
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var invitations = new AsyncFakeRepository<ScopeOwnerInvitation, long>();
        var notices = new RecordingScopeInvitationNoticeSender();
        var audit = new RecordingAuditLogWriter();
        var scope = await SeedScopeAsync(scopes);
        var actor = await SeedPersonAsync(persons, ownedScopes: scope);
        var person = await SeedPersonAsync(persons);
        person.ProcessingRestrictedAt = DateTime.UtcNow.AddDays(-1);
        var handler = Handler(scopes, persons, invitations: invitations, notices: notices, audit: audit);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin, actor.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.Invited);
        Assert.Equal(output.Data.InvitationId, Assert.Single(await AllAsync(invitations)).PublicId);
        Assert.Empty(notices.Invitations);
        Assert.Single(audit.Entries);
    }

    [UnitFact]
    public async Task GivenTheAuditWriteFails_WhenInviting_ThenTheInvitationIsStillAnswered()
    {
        // Given an audit trail that cannot be written — like the decorator, the handler logs and answers
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var invitations = new AsyncFakeRepository<ScopeOwnerInvitation, long>();
        var audit = new Mock<IAuditLogWriter>();
        audit
            .Setup(x => x.WriteAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("audit store down"));
        var scope = await SeedScopeAsync(scopes);
        var actor = await SeedPersonAsync(persons, ownedScopes: scope);
        var person = await SeedPersonAsync(persons);
        var handler = Handler(scopes, persons, invitations: invitations, audit: audit.Object);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin, actor.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Contains(ScopeInvitationMessages.InvitationSent, output.Messages);
        Assert.Single(await AllAsync(invitations));
    }

    [UnitFact]
    public async Task GivenASystemAdminActor_WhenAdding_ThenNoInvitationIsOpenedAndNothingIsSent()
    {
        // Given — the System Admin path is unchanged
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var invitations = new AsyncFakeRepository<ScopeOwnerInvitation, long>();
        var notices = new RecordingScopeInvitationNoticeSender();
        var audit = new RecordingAuditLogWriter();
        var scope = await SeedScopeAsync(scopes);
        var person = await SeedPersonAsync(persons);
        var handler = Handler(scopes, persons, invitations: invitations, notices: notices, audit: audit);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId));

        // Then
        Assert.Contains(PersonMessages.ScopeOwnerAddedSuccessfully, output.Messages);
        Assert.False(output.Data!.Invited);
        Assert.Null(output.Data.InvitationId);
        Assert.Null(output.Data.InvitationExpiresAt);
        Assert.Empty(await AllAsync(invitations));
        Assert.Empty(notices.Invitations);
        Assert.Empty(audit.Entries);
    }

    [UnitFact]
    public async Task GivenAScopeAdminActorAndAPersonAlreadyOwning_WhenAdding_ThenAlreadyOwnerAndNoInvitation()
    {
        // Given AF-21d for a Scope Admin actor
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var invitations = new AsyncFakeRepository<ScopeOwnerInvitation, long>();
        var scope = await SeedScopeAsync(scopes);
        var actor = await SeedPersonAsync(persons, ownedScopes: scope);
        var person = await SeedPersonAsync(persons, ownedScopes: scope);
        var handler = Handler(scopes, persons, invitations: invitations);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin, actor.PublicId));

        // Then
        Assert.Contains(PersonMessages.AlreadyScopeOwner, output.Messages);
        Assert.True(output.Data!.AlreadyOwner);
        Assert.False(output.Data.Invited);
        Assert.Empty(await AllAsync(invitations));
    }

    [UnitFact]
    public async Task GivenAScopeAdminActorAndAnUnknownPerson_WhenAdding_ThenPersonNotValidScopeAdminAndNoInvitation()
    {
        // Given AF-21b for a Scope Admin actor — refused before the invitation path
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var invitations = new AsyncFakeRepository<ScopeOwnerInvitation, long>();
        var notices = new RecordingScopeInvitationNoticeSender();
        var scope = await SeedScopeAsync(scopes);
        var actor = await SeedPersonAsync(persons, ownedScopes: scope);
        var handler = Handler(scopes, persons, invitations: invitations, notices: notices);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, Guid.NewGuid(), Roles.ScopeAdmin, actor.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Equal([PersonMessages.PersonNotValidScopeAdmin], output.Errors);
        Assert.Empty(await AllAsync(invitations));
        Assert.Empty(notices.Invitations);
    }

    [UnitFact]
    public async Task GivenAScopeAdminActorAndALogicallyDeletedPerson_WhenAdding_ThenPersonNotValidScopeAdminAndNoInvitation()
    {
        // Given AF-21b for a Scope Admin actor
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var invitations = new AsyncFakeRepository<ScopeOwnerInvitation, long>();
        var notices = new RecordingScopeInvitationNoticeSender();
        var scope = await SeedScopeAsync(scopes);
        var actor = await SeedPersonAsync(persons, ownedScopes: scope);
        var person = await SeedPersonAsync(persons, isDeleted: true);
        var handler = Handler(scopes, persons, invitations: invitations, notices: notices);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin, actor.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Equal([PersonMessages.PersonNotValidScopeAdmin], output.Errors);
        Assert.Empty(await AllAsync(invitations));
        Assert.Empty(notices.Invitations);
        Assert.Empty((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenAScopeAdminActorAndAUser_WhenAdding_ThenPersonNotValidScopeAdminAndNoInvitation()
    {
        // Given AF-21b for a Scope Admin actor — only an administrator may be invited (FR-SC-08)
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var invitations = new AsyncFakeRepository<ScopeOwnerInvitation, long>();
        var notices = new RecordingScopeInvitationNoticeSender();
        var scope = await SeedScopeAsync(scopes);
        var actor = await SeedPersonAsync(persons, ownedScopes: scope);
        var person = await SeedPersonAsync(persons, Roles.User);
        var handler = Handler(scopes, persons, invitations: invitations, notices: notices);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin, actor.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Equal([PersonMessages.PersonNotValidScopeAdmin], output.Errors);
        Assert.Empty(await AllAsync(invitations));
        Assert.Empty(notices.Invitations);
        Assert.Empty((await StoredAsync(persons, person)).ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenAScopeAdminActorAndALogicallyDeletedScope_WhenAdding_ThenScopeNotFoundAndNoInvitation()
    {
        // Given AF-21a for a Scope Admin actor who owned the scope before it was deleted
        var scopes = new AsyncFakeRepository<Scope, long>();
        var persons = new AsyncFakeRepository<Person, long>();
        var invitations = new AsyncFakeRepository<ScopeOwnerInvitation, long>();
        var notices = new RecordingScopeInvitationNoticeSender();
        var scope = await SeedScopeAsync(scopes, isDeleted: true);
        var actor = await SeedPersonAsync(persons, ownedScopes: scope);
        var person = await SeedPersonAsync(persons);
        var handler = Handler(scopes, persons, invitations: invitations, notices: notices);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, person.PublicId, Roles.ScopeAdmin, actor.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Equal([PersonMessages.ScopeNotFound], output.Errors);
        Assert.Empty(await AllAsync(invitations));
        Assert.Empty(notices.Invitations);
        Assert.Empty((await StoredAsync(persons, person)).ScopeOwnerships);
    }
}
