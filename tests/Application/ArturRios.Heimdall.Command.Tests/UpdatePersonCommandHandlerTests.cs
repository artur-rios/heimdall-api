using ArturRios.Heimdall.Command.Handlers;
using System.Security.Cryptography;
using System.Text;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Hashing;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;
using OtpNet;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for UpdatePersonCommandHandler (UC-08). Cover the main flow for each permitted actor,
// AF-08a (not found), AF-08b (email conflict), AF-08c (role change by a non-System-Admin), the
// unsupported transitions, and the NFR-12 last-owner guard. Also the re-authentication an own email
// change needs (FR-PE-14), the notice the previous address receives (FR-PE-15), and the stamp
// rotation every email change makes (FR-AU-11).
//
// Note: AsyncFakeRepository is an in-memory list and models no EF cascade, so these tests assert
// that the scope navigation was cleared. That the scope_user / scope_owner row actually disappears
// is asserted by PersonControllerUpdateTests against PostgreSQL.
public class UpdatePersonCommandHandlerTests
{
    private const string Password = "Str0ng-Update-Pass!";
    private const string Base32Secret = "JBSWY3DPEHPK3PXP";

    /// <summary>
    ///     One Argon2id derivation for the whole class, shared by every person it builds: at 600 MB a
    ///     derivation, one per test would cost more than the tests themselves.
    /// </summary>
    private static readonly Lazy<(byte[] Hash, byte[] Salt)> Credential = new(() =>
    {
        var hash = Hash.EncodeWithRandomSalt(Password, out var salt);

        return (hash, salt);
    });

    private static Scope Scope(long id) => new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}" };

    private static Person User(long id, Scope scope, string email = "user@test.local") => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"user-{id}",
        Email = email,
        PasswordHash = Credential.Value.Hash,
        Salt = Credential.Value.Salt,
        RoleId = (long)Roles.User,
        EmailVerified = true,
        ScopeId = scope.Id, ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope }
    };

    private static Person ScopeAdmin(long id, string email = "admin@test.local", params Scope[] owned) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"admin-{id}",
        Email = email,
        PasswordHash = Credential.Value.Hash,
        Salt = Credential.Value.Salt,
        RoleId = (long)Roles.ScopeAdmin,
        EmailVerified = true,
        ScopeOwnerships = owned.Select(scope => new ScopeOwner { ScopeId = scope.Id, Scope = scope }).ToList()
    };

    private static IValidator<UpdatePersonCommand> PassingValidator()
    {
        var validator = new Mock<IValidator<UpdatePersonCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<UpdatePersonCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        return validator.Object;
    }

    private static IScopeOwnershipChecker Ownership(bool allowed)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);

        return checker.Object;
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

    /// <summary>The second-factor state a handler is built over, and the notices it sends.</summary>
    private sealed class TwoFactorState
    {
        public AsyncFakeRepository<TwoFactorAuth, long> Configurations { get; } = new();
        public AsyncFakeRepository<TwoFactorEmailCode, long> EmailCodes { get; } = new();
        public AsyncFakeRepository<TwoFactorRecoveryCode, long> RecoveryCodes { get; } = new();
        public List<string> NoticesSentTo { get; } = [];

        public ITwoFactorFactorVerifier Verifier()
        {
            var protector = new Mock<ITotpSecretProtector>();
            protector.Setup(p => p.Unprotect(It.IsAny<byte[]>())).Returns(Base32Secret);

            return new TwoFactorFactorVerifier(
                EmailCodes, InMemoryAtomicWrites.Instance, RecoveryCodes,
                new TotpCodeVerifier(protector.Object, InMemoryAtomicWrites.Instance));
        }

        public IEmailChangeNoticeSender NoticeSender()
        {
            var sender = new Mock<IEmailChangeNoticeSender>();
            sender
                .Setup(x => x.SendAsync(It.IsAny<string>()))
                .Callback<string>(NoticesSentTo.Add)
                .Returns(Task.CompletedTask);

            return sender.Object;
        }

        /// <summary>
        ///     An active App configuration for <paramref name="person" />, holding one recovery code.
        ///     Called once the person is in its repository, which is what assigns the Id it is keyed by.
        /// </summary>
        public async Task<string> ActivateAsync(Person person)
        {
            var configuration = new TwoFactorAuth
            {
                PersonId = person.Id, IsActive = true, AppEnabled = true, TotpSecretEncrypted = [1, 2, 3]
            };
            await Configurations.CreateAsync(configuration);

            const string recoveryCode = "AAAA-BBBB";
            await RecoveryCodes.CreateAsync(new TwoFactorRecoveryCode
            {
                TwoFactorAuthId = configuration.Id,
                CodeHash = SHA256.HashData(Encoding.UTF8.GetBytes(recoveryCode))
            });

            return recoveryCode;
        }
    }

    private static string CurrentTotpCode() => new Totp(Base32Encoding.ToBytes(Base32Secret)).ComputeTotp();

    private static UpdatePersonCommandHandler HandlerFor(
        AsyncFakeRepository<Person, long> persons,
        bool ownershipAllowed = true,
        AsyncFakeRepository<GoogleUser, long>? googleUsers = null,
        AsyncFakeRepository<EmailVerificationToken, long>? verificationTokens = null,
        TwoFactorState? twoFactor = null)
    {
        twoFactor ??= new TwoFactorState();

        return new UpdatePersonCommandHandler(
            PassingValidator(),
            persons,
            googleUsers ?? new AsyncFakeRepository<GoogleUser, long>(),
            persons,
            Ownership(ownershipAllowed),
            verificationTokens ?? new AsyncFakeRepository<EmailVerificationToken, long>(),
            InMemoryAtomicWrites.Instance,
            twoFactor.Configurations,
            twoFactor.Verifier(),
            twoFactor.NoticeSender());
    }

    /// <summary>
    ///     A command resubmitting <paramref name="target" /> as it is, carrying the shared password.
    ///     The password is ignored unless the actor changes their own email (FR-PE-14), so the tests
    ///     that are not about re-authentication are unaffected by it; the ones that are set it.
    /// </summary>
    private static UpdatePersonCommand CommandFor(Person target, int actingRole, Guid actingPersonId) => new()
    {
        Id = target.PublicId,
        Name = target.Name,
        Email = target.Email,
        CurrentPassword = Password,
        ActingRole = actingRole,
        ActingPersonId = actingPersonId
    };

    [UnitFact]
    public async Task GivenSystemAdminActor_WhenUpdatingNameAndEmail_ThenPersonIsUpdated()
    {
        // Given
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());
        command.Name = "Renamed";
        command.Email = "renamed@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal("Renamed", output.Data!.Name);
        Assert.Equal("renamed@test.local", output.Data.Email);
        Assert.False(output.Data.EmailVerified);
        Assert.Contains(PersonMessages.PersonUpdatedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenUserUpdatingSelf_WhenUpdatingName_ThenPersonIsUpdated()
    {
        // Given
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        command.Name = "Renamed";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal("Renamed", output.Data!.Name);
    }

    [UnitFact]
    public async Task GivenAnOutstandingVerificationToken_WhenEmailChanges_ThenTheTokenIsRetired()
    {
        // Given a person holding a live verification token mailed to their current address
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var tokens = new AsyncFakeRepository<EmailVerificationToken, long>();
        await tokens.CreateAsync(new EmailVerificationToken
        {
            PersonId = target.Id, TokenHash = "old-address", ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        var handler = HandlerFor(persons, verificationTokens: tokens);
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        command.Email = "elsewhere@test.local";

        // When the address changes
        var output = await handler.HandleAsync(command);

        // Then the token can no longer verify the new address on the old one's behalf
        Assert.True(output.Success);
        Assert.All(tokens.Query().ToList(), token => Assert.True(token.Used));
    }

    [UnitFact]
    public async Task GivenAnOutstandingVerificationToken_WhenEmailIsUnchanged_ThenTheTokenStaysLive()
    {
        // Given
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var tokens = new AsyncFakeRepository<EmailVerificationToken, long>();
        await tokens.CreateAsync(new EmailVerificationToken
        {
            PersonId = target.Id, TokenHash = "same-address", ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        var handler = HandlerFor(persons, verificationTokens: tokens);
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        command.Name = "Renamed";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.All(tokens.Query().ToList(), token => Assert.False(token.Used));
    }

    [UnitFact]
    public async Task GivenUnchangedEmail_WhenUpdating_ThenEmailVerifiedIsPreserved()
    {
        // Given a verified person whose email is resubmitted unchanged
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());
        command.Name = "Renamed";

        // When
        var output = await handler.HandleAsync(command);

        // Then — no false conflict, and the verification flag survives
        Assert.True(output.Success);
        Assert.True(output.Data!.EmailVerified);
    }

    [UnitFact]
    public async Task GivenOwningScopeAdmin_WhenUpdatingScopeUser_ThenPersonIsUpdated()
    {
        // Given a ScopeAdmin who owns the target User's scope
        var scope = Scope(1);
        var target = User(10, scope);
        var actor = ScopeAdmin(11, "owner@test.local", scope);
        var persons = await PersonsWith(target, actor);
        var handler = HandlerFor(persons, ownershipAllowed: true);
        var command = CommandFor(target, (int)Roles.ScopeAdmin, actor.PublicId);
        command.Name = "Renamed";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal("Renamed", output.Data!.Name);
    }

    [UnitFact]
    public async Task GivenNonOwningScopeAdmin_WhenUpdatingScopeUser_ThenReturnsNotAuthorized()
    {
        // Given a ScopeAdmin the ownership checker rejects
        var scope = Scope(1);
        var target = User(10, scope);
        var actor = ScopeAdmin(11, "outsider@test.local");
        var persons = await PersonsWith(target, actor);
        var handler = HandlerFor(persons, ownershipAllowed: false);

        // When
        var output = await handler.HandleAsync(CommandFor(target, (int)Roles.ScopeAdmin, actor.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotAuthorizedToUpdatePerson, output.Errors);
    }

    [UnitFact]
    public async Task GivenUserUpdatingAnotherPerson_WhenUpdating_ThenReturnsNotAuthorized()
    {
        // Given two Users of the same scope
        var scope = Scope(1);
        var target = User(10, scope);
        var actor = User(11, scope, "other@test.local");
        var persons = await PersonsWith(target, actor);
        var handler = HandlerFor(persons);

        // When
        var output = await handler.HandleAsync(CommandFor(target, (int)Roles.User, actor.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotAuthorizedToUpdatePerson, output.Errors);
    }

    [UnitFact]
    public async Task GivenUnknownPersonId_WhenUpdating_ThenReturnsPersonNotFound()
    {
        // Given an empty store (AF-08a)
        var persons = await PersonsWith();
        var handler = HandlerFor(persons);

        // When
        var output = await handler.HandleAsync(new UpdatePersonCommand
        {
            Id = Guid.NewGuid(), Name = "Ana", Email = "ana@test.local",
            ActingRole = (int)Roles.SystemAdmin, ActingPersonId = Guid.NewGuid()
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedPerson_WhenUpdating_ThenReturnsPersonNotFound()
    {
        // Given a logically deleted person (AF-08a)
        var scope = Scope(1);
        var target = User(10, scope);
        target.IsDeleted = true;
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);

        // When
        var output = await handler.HandleAsync(CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenEmailTakenByAnotherUserInScope_WhenUpdating_ThenReturnsEmailAlreadyExists()
    {
        // Given two Users in one scope (AF-08b, FR-PE-09 within scope)
        var scope = Scope(1);
        var target = User(10, scope);
        var other = User(11, scope, "taken@test.local");
        var persons = await PersonsWith(target, other);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.Email = "taken@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
    }

    [UnitFact]
    public async Task GivenEmailTakenByAnotherAdmin_WhenUpdatingAdmin_ThenReturnsEmailAlreadyExists()
    {
        // Given two admins (AF-08b, FR-PE-09 system-wide)
        var scope = Scope(1);
        var target = ScopeAdmin(10, "admin@test.local", scope);
        var other = ScopeAdmin(11, "taken@test.local", scope);
        var persons = await PersonsWith(target, other);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.Email = "taken@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
    }

    [UnitFact]
    public async Task GivenRoleChangeOntoAnAddressAnAdminHolds_WhenUpdating_ThenReturnsEmailAlreadyExists()
    {
        // Given a User and an administrator who share an address. That is legal while they are in
        // different namespaces — a User's address is unique within their scope, an admin's is unique
        // system-wide — but promoting the User to System Admin moves them into the admin namespace,
        // where the address is already taken.
        //
        // The uniqueness check used to run only when the *email* changed, so this promotion, which
        // does not touch the address, slipped past it. UC-11's admin lookup then resolves one of the
        // two rows and stops, leaving the other person unable to log in at all.
        var scope = Scope(1);
        var target = User(10, scope, "shared@test.local");
        var existingAdmin = ScopeAdmin(11, "shared@test.local");
        var persons = await PersonsWith(target, existingAdmin);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.RoleId = (int)Roles.SystemAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);

        // Then — nothing was written: the target keeps their role and their scope membership
        var stored = persons.Query().ToList().Single(person => person.Id == target.Id);
        Assert.Equal((long)Roles.User, stored.RoleId);
        Assert.NotNull(stored.ScopeMembership);
    }

    [UnitFact]
    public async Task GivenRoleChangeOntoAFreeAddress_WhenUpdating_ThenThePromotionSucceeds()
    {
        // Given the same promotion where nobody else holds the address. The re-check must not turn
        // an ordinary promotion into a conflict — the person being updated is excluded from it.
        var scope = Scope(1);
        var target = User(10, scope, "solo@test.local");
        var persons = await PersonsWith(target, ScopeAdmin(11, "someone-else@test.local"));
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.RoleId = (int)Roles.SystemAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);

        var stored = persons.Query().ToList().Single(person => person.Id == target.Id);
        Assert.Equal((long)Roles.SystemAdmin, stored.RoleId);
        Assert.Null(stored.ScopeMembership);

        // Then — the address was not touched, so verification survives (only an email change clears it)
        Assert.True(stored.EmailVerified);
    }

    [UnitFact]
    public async Task GivenEmailHeldByAGoogleUserOfTheScope_WhenUpdatingAUser_ThenReturnsEmailAlreadyExists()
    {
        // Given a scope whose address space it shares with its Google Users (FR-GO-07): moving a
        // User onto an address a Google User holds is the same conflict as moving them onto another
        // User's.
        var scope = Scope(1);
        var target = User(10, scope, "user@test.local");
        var persons = await PersonsWith(target);
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        await googleUsers.CreateAsync(new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = "google-sub",
            Email = "TAKEN@test.local",
            ScopeId = scope.Id
        });

        var handler = HandlerFor(persons, googleUsers: googleUsers);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.Email = "taken@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.EmailAlreadyExists, output.Errors);
        Assert.Equal("user@test.local", persons.Query().ToList().Single(x => x.Id == target.Id).Email);
    }

    [UnitFact]
    public async Task GivenNonSystemAdminActor_WhenChangingRole_ThenReturnsRoleChangeRequiresSystemAdmin()
    {
        // Given an owning ScopeAdmin attempting a role change (AF-08c)
        var scope = Scope(1);
        var target = User(10, scope);
        var actor = ScopeAdmin(11, "owner@test.local", scope);
        var persons = await PersonsWith(target, actor);
        var handler = HandlerFor(persons, ownershipAllowed: true);
        var command = CommandFor(target, (int)Roles.ScopeAdmin, actor.PublicId);
        command.RoleId = (int)Roles.SystemAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.RoleChangeRequiresSystemAdmin, output.Errors);
    }

    [UnitFact]
    public async Task GivenSystemAdminPromotingUserToScopeAdmin_WhenUpdating_ThenReturnsUnsupportedTransition()
    {
        // Given a User being pushed to ScopeAdmin, which would need a target scope
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.RoleId = (int)Roles.ScopeAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.UnsupportedRoleTransition, output.Errors);
    }

    [UnitFact]
    public async Task GivenSystemAdminPromotingUserToSystemAdmin_WhenUpdating_ThenScopeMembershipIsCleared()
    {
        // Given a User promoted to SystemAdmin, who must end up with no scope (FR-PE-10)
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.RoleId = (int)Roles.SystemAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal((int)Roles.SystemAdmin, output.Data!.Role);
        Assert.Null(output.Data.ScopeId);
        Assert.Null(target.ScopeMembership);
    }

    [UnitFact]
    public async Task GivenScopeWithAnotherOwner_WhenPromotingOwnerToSystemAdmin_ThenOwnershipsAreCleared()
    {
        // Given a scope with two owners, so losing one leaves it owned (NFR-12 satisfied)
        var scope = Scope(1);
        var target = ScopeAdmin(10, "first@test.local", scope);
        var coOwner = ScopeAdmin(11, "second@test.local", scope);
        var persons = await PersonsWith(target, coOwner);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.RoleId = (int)Roles.SystemAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal((int)Roles.SystemAdmin, output.Data!.Role);
        Assert.Empty(output.Data.OwnedScopeIds);
        Assert.Empty(target.ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenSoleOwner_WhenPromotingToSystemAdmin_ThenReturnsScopeWouldLoseLastOwner()
    {
        // Given a scope whose only owner is the target (NFR-12)
        var scope = Scope(1);
        var target = ScopeAdmin(10, "only@test.local", scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.RoleId = (int)Roles.SystemAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeWouldLoseLastOwner, output.Errors);
    }

    [UnitFact]
    public async Task GivenOnlyCoOwnerIsDeleted_WhenPromotingOwnerToSystemAdmin_ThenReturnsScopeWouldLoseLastOwner()
    {
        // Given a scope owned by the target and one logically deleted co-owner. A soft-deleted owner
        // can no longer authenticate, so the scope would be left effectively ownerless — the same
        // reading UC-09 AF-09e and UC-10 AF-10b apply (NFR-12).
        var scope = Scope(1);
        var target = ScopeAdmin(10, "first@test.local", scope);
        var deletedCoOwner = ScopeAdmin(11, "second@test.local", scope);
        deletedCoOwner.IsDeleted = true;
        var persons = await PersonsWith(target, deletedCoOwner);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.RoleId = (int)Roles.SystemAdmin;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeWouldLoseLastOwner, output.Errors);
        Assert.Equal((long)Roles.ScopeAdmin, target.RoleId);
        Assert.Single(target.ScopeOwnerships);
    }

    [UnitFact]
    public async Task GivenNullRoleId_WhenUpdating_ThenRoleIsUnchanged()
    {
        // Given a User whose command carries no role
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var handler = HandlerFor(persons);
        var command = CommandFor(target, (int)Roles.SystemAdmin, Guid.NewGuid());
        command.Name = "Renamed";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal((int)Roles.User, output.Data!.Role);
        Assert.NotNull(target.ScopeMembership);
    }

    // FR-AU-11: a new address is a credential change, wherever it comes from; a new name is not.

    [UnitFact]
    public async Task GivenSystemAdminChangingAnotherPersonsEmail_WhenHandlingUpdate_ThenTheirSecurityStampIsReplaced()
    {
        // Given
        var target = User(10, Scope(1));
        var stamp = target.SecurityStamp;
        var handler = HandlerFor(await PersonsWith(target));
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());
        command.Email = "moved@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.NotEqual(stamp, target.SecurityStamp);
    }

    [UnitFact]
    public async Task GivenANameOnlyUpdate_WhenHandlingUpdate_ThenTheSecurityStampIsKept()
    {
        // Given
        var target = User(10, Scope(1));
        var stamp = target.SecurityStamp;
        var handler = HandlerFor(await PersonsWith(target));
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());
        command.Name = "Renamed";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal(stamp, target.SecurityStamp);
    }

    [UnitFact]
    public async Task GivenAnEmailChangeOnlyInCase_WhenHandlingUpdate_ThenItCountsAsAChange()
    {
        // Given — a change is detected ordinally, as stored, so a different casing is a different
        // address: it fails closed, revoking and notifying rather than letting a variant slip through
        var target = User(10, Scope(1), email: "user@test.local");
        var stamp = target.SecurityStamp;
        var twoFactor = new TwoFactorState();
        var handler = HandlerFor(await PersonsWith(target), twoFactor: twoFactor);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());
        command.Email = "USER@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.NotEqual(stamp, target.SecurityStamp);
        Assert.False(target.EmailVerified);
        Assert.Equal(["user@test.local"], twoFactor.NoticesSentTo);
    }

    [UnitFact]
    public async Task GivenOnlySurroundingWhitespace_WhenHandlingUpdate_ThenItIsNoChangeAndNothingNewIsStored()
    {
        // Given — the comparison is of the trimmed values, and the padding is not written either
        var target = User(10, Scope(1), email: "user@test.local");
        var stamp = target.SecurityStamp;
        var handler = HandlerFor(await PersonsWith(target));
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        command.Email = " user@test.local ";
        command.CurrentPassword = null;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal("user@test.local", target.Email);
        Assert.Equal(stamp, target.SecurityStamp);
        Assert.True(target.EmailVerified);
    }

    [UnitFact]
    public async Task GivenOwnChangeToAVariantACaseInsensitiveComparisonFolds_WhenNoPassword_ThenRefused()
    {
        // Given — final and medial sigma are distinct characters that OrdinalIgnoreCase treats as
        // equal; read that way, this change would have skipped re-authentication altogether
        var target = User(10, Scope(1), email: "a@x\u03c3.gr");
        var stamp = target.SecurityStamp;
        var twoFactor = new TwoFactorState();
        var handler = HandlerFor(await PersonsWith(target), twoFactor: twoFactor);
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        command.Email = "a@x\u03c2.gr";
        command.CurrentPassword = null;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.PasswordOrCodeIncorrect, output.Errors);
        Assert.Equal("a@x\u03c3.gr", target.Email);
        Assert.Equal(stamp, target.SecurityStamp);
        Assert.Empty(twoFactor.NoticesSentTo);
    }

    [UnitFact]
    public async Task GivenSystemAdminChangingTheirOwnEmailWithoutPassword_WhenHandlingUpdate_ThenRefused()
    {
        // Given — the role grants nothing here: their own address is re-authenticated like anyone's
        var admin = ScopeAdmin(10, email: "root@test.local");
        admin.RoleId = (long)Roles.SystemAdmin;
        var stamp = admin.SecurityStamp;
        var handler = HandlerFor(await PersonsWith(admin));
        var command = CommandFor(admin, (int)Roles.SystemAdmin, actingPersonId: admin.PublicId);
        command.Email = "elsewhere@test.local";
        command.CurrentPassword = null;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.PasswordOrCodeIncorrect, output.Errors);
        Assert.Equal("root@test.local", admin.Email);
        Assert.Equal(stamp, admin.SecurityStamp);
    }

    [UnitFact]
    public async Task GivenSystemAdminChangingTheirOwnEmailWithPassword_WhenHandlingUpdate_ThenUpdatedAndTheStampRotated()
    {
        // Given
        var admin = ScopeAdmin(10, email: "root@test.local");
        admin.RoleId = (long)Roles.SystemAdmin;
        var stamp = admin.SecurityStamp;
        var handler = HandlerFor(await PersonsWith(admin));
        var command = CommandFor(admin, (int)Roles.SystemAdmin, actingPersonId: admin.PublicId);
        command.Email = "elsewhere@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal("elsewhere@test.local", admin.Email);
        Assert.NotEqual(stamp, admin.SecurityStamp);
    }

    // FR-PE-14: changing your own email needs your password, and your second factor when active.

    [UnitFact]
    public async Task GivenUserChangingOwnEmailWithoutPassword_WhenHandlingUpdate_ThenRefusedAndNothingChanges()
    {
        // Given
        var target = User(10, Scope(1));
        var stamp = target.SecurityStamp;
        var twoFactor = new TwoFactorState();
        var handler = HandlerFor(await PersonsWith(target), twoFactor: twoFactor);
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        command.Email = "taken-over@test.local";
        command.CurrentPassword = null;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.PasswordOrCodeIncorrect, output.Errors);
        Assert.Equal("user@test.local", target.Email);
        Assert.True(target.EmailVerified);
        Assert.Equal(stamp, target.SecurityStamp);
        Assert.Empty(twoFactor.NoticesSentTo);
    }

    [UnitFact]
    public async Task GivenUserChangingOwnEmailWithWrongPassword_WhenHandlingUpdate_ThenRefused()
    {
        // Given
        var target = User(10, Scope(1));
        var handler = HandlerFor(await PersonsWith(target));
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        command.Email = "taken-over@test.local";
        command.CurrentPassword = "not-the-password";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.PasswordOrCodeIncorrect, output.Errors);
        Assert.Equal("user@test.local", target.Email);
    }

    [UnitFact]
    public async Task GivenOwnEmailChangeWithPasswordButNoSecondFactor_WhenTwoFactorIsActive_ThenRefused()
    {
        // Given
        var target = User(10, Scope(1));
        var persons = await PersonsWith(target);
        var twoFactor = new TwoFactorState();
        await twoFactor.ActivateAsync(target);
        var handler = HandlerFor(persons, twoFactor: twoFactor);
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        command.Email = "taken-over@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.PasswordOrCodeIncorrect, output.Errors);
        Assert.Equal("user@test.local", target.Email);
    }

    [UnitFact]
    public async Task GivenOwnEmailChangeWithPasswordAndAWrongCode_WhenTwoFactorIsActive_ThenRefused()
    {
        // Given
        var target = User(10, Scope(1));
        var persons = await PersonsWith(target);
        var twoFactor = new TwoFactorState();
        await twoFactor.ActivateAsync(target);
        var handler = HandlerFor(persons, twoFactor: twoFactor);
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        command.Email = "taken-over@test.local";
        command.TwoFactorCode = "000000";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.PasswordOrCodeIncorrect, output.Errors);
    }

    [UnitFact]
    public async Task GivenOwnEmailChangeWithPasswordAndAppCode_WhenTwoFactorIsActive_ThenUpdated()
    {
        // Given
        var target = User(10, Scope(1));
        var persons = await PersonsWith(target);
        var twoFactor = new TwoFactorState();
        await twoFactor.ActivateAsync(target);
        var handler = HandlerFor(persons, twoFactor: twoFactor);
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        command.Email = "moved@test.local";
        command.TwoFactorCode = CurrentTotpCode();

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal("moved@test.local", output.Data!.Email);
    }

    [UnitFact]
    public async Task GivenOwnEmailChangeWithPasswordAndRecoveryCode_WhenTwoFactorIsActive_ThenUpdatedAndTheCodeIsSpent()
    {
        // Given
        var target = User(10, Scope(1));
        var persons = await PersonsWith(target);
        var twoFactor = new TwoFactorState();
        var recoveryCode = await twoFactor.ActivateAsync(target);
        var handler = HandlerFor(persons, twoFactor: twoFactor);
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        command.Email = "moved@test.local";
        command.TwoFactorCode = recoveryCode;

        // When
        var output = await handler.HandleAsync(command);

        // Then — a recovery code is good once, here as everywhere
        Assert.True(output.Success);
        Assert.True(Assert.Single(twoFactor.RecoveryCodes.Query().ToList()).Used);
    }

    [UnitFact]
    public async Task GivenOwnEmailChangeWithThePassword_WhenTwoFactorIsNotActive_ThenUpdated()
    {
        // Given
        var target = User(10, Scope(1));
        var handler = HandlerFor(await PersonsWith(target));
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        command.Email = "moved@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal("moved@test.local", output.Data!.Email);
        Assert.False(output.Data.EmailVerified);
    }

    [UnitFact]
    public async Task GivenOwnEmailChangeToATakenAddressWithoutPassword_WhenHandlingUpdate_ThenThePasswordIsAskedForFirst()
    {
        // Given — without the password the caller must not learn whether an address is taken
        var scope = Scope(1);
        var target = User(10, scope);
        var other = User(11, scope, email: "taken@test.local");
        var handler = HandlerFor(await PersonsWith(target, other));
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        command.Email = "taken@test.local";
        command.CurrentPassword = null;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.Contains(AuthMessages.PasswordOrCodeIncorrect, output.Errors);
        Assert.DoesNotContain(PersonMessages.EmailAlreadyExists, output.Errors);
    }

    [UnitFact]
    public async Task GivenSystemAdminChangingAnotherPersonsEmailWithoutPassword_WhenHandlingUpdate_ThenUpdated()
    {
        // Given — the administrator's own authorisation covers it; the target's credential is not theirs
        var target = User(10, Scope(1));
        var persons = await PersonsWith(target);
        var twoFactor = new TwoFactorState();
        await twoFactor.ActivateAsync(target);
        var handler = HandlerFor(persons, twoFactor: twoFactor);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());
        command.Email = "moved@test.local";
        command.CurrentPassword = null;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal("moved@test.local", target.Email);
    }

    [UnitFact]
    public async Task GivenUserRenamingThemselvesWithoutPassword_WhenHandlingUpdate_ThenUpdated()
    {
        // Given — no credential changes, so none is asked for
        var target = User(10, Scope(1));
        var handler = HandlerFor(await PersonsWith(target));
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        command.Name = "Renamed";
        command.CurrentPassword = null;

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal("Renamed", target.Name);
    }

    // FR-PE-15: the previous address is told, and told nothing about the new one.

    [UnitFact]
    public async Task GivenOwnEmailChange_WhenHandlingUpdate_ThenThePreviousAddressIsNotified()
    {
        // Given
        var target = User(10, Scope(1), email: "before@test.local");
        var twoFactor = new TwoFactorState();
        var handler = HandlerFor(await PersonsWith(target), twoFactor: twoFactor);
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        command.Email = "after@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal(["before@test.local"], twoFactor.NoticesSentTo);
    }

    [UnitFact]
    public async Task GivenAdministratorEmailChange_WhenHandlingUpdate_ThenThePreviousAddressIsNotified()
    {
        // Given
        var target = User(10, Scope(1), email: "before@test.local");
        var twoFactor = new TwoFactorState();
        var handler = HandlerFor(await PersonsWith(target), twoFactor: twoFactor);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());
        command.Email = "after@test.local";

        // When
        await handler.HandleAsync(command);

        // Then
        Assert.Equal(["before@test.local"], twoFactor.NoticesSentTo);
    }

    [UnitFact]
    public async Task GivenNoEmailChange_WhenHandlingUpdate_ThenNoNoticeIsSent()
    {
        // Given
        var target = User(10, Scope(1));
        var twoFactor = new TwoFactorState();
        var handler = HandlerFor(await PersonsWith(target), twoFactor: twoFactor);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());
        command.Name = "Renamed";

        // When
        await handler.HandleAsync(command);

        // Then
        Assert.Empty(twoFactor.NoticesSentTo);
    }

    // A stored address never carries surrounding whitespace: sign-in and recovery match it exactly
    // (case aside), so a padded copy would no longer be found.

    [UnitFact]
    public async Task GivenAPaddedNewAddress_WhenHandlingUpdate_ThenTheTrimmedAddressIsStored()
    {
        // Given
        var target = User(10, Scope(1), email: "before@test.local");
        var twoFactor = new TwoFactorState();
        var handler = HandlerFor(await PersonsWith(target), twoFactor: twoFactor);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());
        command.Email = "  after@test.local ";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal("after@test.local", target.Email);
        Assert.Equal("after@test.local", output.Data!.Email);
        Assert.Equal(["before@test.local"], twoFactor.NoticesSentTo);
    }

    // FR-AU-11: the stamp is replaced before the save, so a failed save leaves tokens revoked, and
    // again after it, so a token issued while the save was in flight — under the first new stamp,
    // with the old address — does not survive the change either.

    [UnitFact]
    public async Task GivenAnEmailChange_WhenHandlingUpdate_ThenTheStampInPlaceDuringTheSaveIsReplacedToo()
    {
        // Given a writer that records the stamp a concurrent sign-in would have read during the save
        var target = User(10, Scope(1));
        var original = target.SecurityStamp;
        var persons = await PersonsWith(target);
        Guid? duringSave = null;

        var writer = new Mock<ArturRios.Data.Relational.Core.Interfaces.IAsyncRepository<Person, long>>();
        writer
            .Setup(x => x.UpdateAsync(It.IsAny<Person>(), It.IsAny<CancellationToken>()))
            .Returns((Person person, CancellationToken ct) =>
            {
                duringSave = person.SecurityStamp;
                return persons.UpdateAsync(person, ct);
            });

        var twoFactor = new TwoFactorState();
        var handler = new UpdatePersonCommandHandler(
            PassingValidator(), persons, new AsyncFakeRepository<GoogleUser, long>(), writer.Object,
            Ownership(true), new AsyncFakeRepository<EmailVerificationToken, long>(),
            InMemoryAtomicWrites.Instance, twoFactor.Configurations, twoFactor.Verifier(),
            twoFactor.NoticeSender());
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());
        command.Email = "moved@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then — rotated before the save, and again after it
        Assert.True(output.Success);
        Assert.NotNull(duringSave);
        Assert.NotEqual(original, duringSave);
        Assert.NotEqual(duringSave, target.SecurityStamp);
        Assert.NotEqual(original, target.SecurityStamp);
    }

    // FR-AU-13: the re-authentication budget

    [UnitFact]
    public async Task GivenAWrongPasswordAndAWrongCode_WhenEachIsPresented_ThenTheAnswersAreIdentical()
    {
        // Given
        var target = User(10, Scope(1));
        var persons = await PersonsWith(target);
        var twoFactor = new TwoFactorState();
        await twoFactor.ActivateAsync(target);
        var handler = HandlerFor(persons, twoFactor: twoFactor);

        var wrongPassword = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        wrongPassword.Email = "moved@test.local";
        wrongPassword.CurrentPassword = "not-the-password";
        wrongPassword.TwoFactorCode = CurrentTotpCode();

        var wrongCode = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        wrongCode.Email = "moved@test.local";
        wrongCode.TwoFactorCode = "000000";

        // When
        var first = await handler.HandleAsync(wrongPassword);
        var second = await handler.HandleAsync(wrongCode);

        // Then
        Assert.Equal([AuthMessages.PasswordOrCodeIncorrect], first.Errors);
        Assert.Equal(first.Errors, second.Errors);
    }

    [UnitFact]
    public async Task GivenAPausedBudget_WhenTheRightPasswordIsPresented_ThenTheEmailIsNotChanged()
    {
        // Given
        var target = User(10, Scope(1));
        target.ReauthenticationPausedUntil = DateTime.UtcNow.AddMinutes(10);
        var stamp = target.SecurityStamp;
        var twoFactor = new TwoFactorState();
        var handler = HandlerFor(await PersonsWith(target), twoFactor: twoFactor);
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        command.Email = "moved@test.local";

        // When
        var output = await handler.HandleAsync(command);

        // Then
        Assert.Equal([AuthMessages.PasswordOrCodeIncorrect], output.Errors);
        Assert.Equal("user@test.local", target.Email);
        Assert.Equal(stamp, target.SecurityStamp);
        Assert.Empty(twoFactor.NoticesSentTo);
    }

    [UnitFact]
    public async Task GivenAWrongPassword_WhenChangingTheOwnEmail_ThenTheFailureIsCountedAndTheSignInBudgetsAreUntouched()
    {
        // Given
        var target = User(10, Scope(1));
        var handler = HandlerFor(await PersonsWith(target));
        var command = CommandFor(target, (int)Roles.User, actingPersonId: target.PublicId);
        command.Email = "moved@test.local";
        command.CurrentPassword = "not-the-password";

        // When
        await handler.HandleAsync(command);

        // Then
        Assert.Equal(1, target.FailedReauthenticationAttempts);
        Assert.Equal(0, target.FailedLoginAttempts);
        Assert.Equal(0, target.FailedSecondFactorAttempts);
    }
}
